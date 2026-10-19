import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, DatePicker, Empty, Input, Pagination, Select, Spin, Tabs, Tag, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { useTranslation } from 'react-i18next'
import { ContainerNumberRevealButton, useContainerNumberDisplay } from '../../../components/ContainerNumber'
import PageContainer from '../../../components/PageContainer'
import {
  getWarehouseProductFlowOptions,
  queryWarehouseProductFlowBranchDaily,
  queryWarehouseProductFlowBranches,
  queryWarehouseProductFlowCandidates,
  queryWarehouseProductFlowContainers,
  queryWarehouseProductFlowOrderShipmentDaily,
  queryWarehouseProductFlowOrders,
  queryWarehouseProductFlowDaily,
  queryWarehouseProductFlowSalesDaily,
  queryWarehouseProductFlowShipments,
  queryWarehouseProductFlowSummary,
} from '../../../services/warehouseProductFlowAnalysisService'
import { getCategoryTree, type WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import type {
  WarehouseProductFlowBranch,
  WarehouseProductFlowCandidate,
  WarehouseProductFlowCandidatesData,
  WarehouseProductFlowDaily,
  WarehouseProductFlowFilter,
  WarehouseProductFlowMetrics,
  WarehouseProductFlowPeriod,
  WarehouseProductFlowPeriods,
  WarehouseProductFlowSelection,
  WarehouseProductFlowShipmentRow,
  WarehouseProductFlowSummaryData,
} from '../../../types/warehouseProductFlowAnalysis'
import { createLatestRequestGuard } from '../../../utils/latestRequestGuard'
import ProductImage from '../ProductFlowShared/ProductImage'
import FlowTrendChart from '../ProductFlowShared/FlowTrendChart'
import { createAllFilteredSelection, createIncludedSelection, isProductSelected, resolveCurrentProductCode, selectFirstCandidate, toggleProductSelection } from '../ProductFlowShared/logic'
import { buildWarehouseProductFlowCategoryOptions, buildWarehouseProductFlowDefaultPeriods, createWarehouseProductFlowFilter, filterWarehouseProductFlowCategoryOptions, filterWarehouseProductFlowShipments, filterWarehouseProductFlowSupplierOptions, isValidWarehouseProductFlowRange, sortWarehouseProductFlowBranches, type WarehouseProductFlowPosFilter } from './logic'
import styles from './index.module.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

const { RangePicker } = DatePicker
const numberFormatter = new Intl.NumberFormat('en-AU')
const moneyFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' })

function emptyMetrics(): WarehouseProductFlowMetrics {
  return { inboundQuantity: 0, orderedQuantity: 0, shippedQuantity: 0, netSalesQuantity: 0, netSalesAmount: 0, averageUnitPrice: null }
}

type LocalizedError = { key: string; diagnostic?: string }

function localizedError(error: unknown, key: string): LocalizedError {
  return { key, diagnostic: error instanceof Error && error.message ? error.message : undefined }
}

function isAbortError(error: unknown) {
  return error instanceof Error && error.name === 'AbortError'
}

function toRange(period: WarehouseProductFlowPeriod): [Dayjs, Dayjs] {
  return [dayjs(period.startDate), dayjs(period.endDate)]
}

function toPeriod(range: [Dayjs, Dayjs]): WarehouseProductFlowPeriod {
  return { startDate: range[0].format('YYYY-MM-DD'), endDate: range[1].format('YYYY-MM-DD') }
}

function LocalPanel({ loading, error, retry, children }: { loading: boolean; error?: LocalizedError; retry: () => void; children: React.ReactNode }) {
  const { t } = useTranslation()
  if (loading) return <div className={styles.loading}><Spin /></div>
  if (error) return <Alert type="error" showIcon message={t('warehouseProductFlowAnalysis.errors.loadFailed')} description={<LocalizedErrorDescription error={error} />} action={<Button size="small" onClick={retry}>{t('common.retry')}</Button>} />
  return <>{children}</>
}

function LocalizedErrorDescription({ error }: { error: LocalizedError }) {
  const { t } = useTranslation()
  return <>{t(error.key)}</>
}

function formatPeriodLabel(period: WarehouseProductFlowPeriod) {
  return `${period.startDate} – ${period.endDate}`
}

function StageCard({ step, label, value, tone, period }: { step: number; label: string; value: number; tone: string; period: WarehouseProductFlowPeriod }) {
  return <div className={styles.stageCard}><span className={`${styles.stageNumber} ${tone}`}>{step}</span><div><span className={styles.stageLabel}>{label}</span><strong>{numberFormatter.format(value)}</strong><small className={styles.stagePeriod}><span>{period.startDate}</span>{' – '}<span>{period.endDate}</span></small></div></div>
}

function QuantityTrend({ data, firstKey, firstLabel, secondKey, secondLabel, ariaLabel }: {
  data: WarehouseProductFlowDaily[]
  firstKey: 'inboundQuantity' | 'orderedQuantity' | 'netSalesQuantity'
  firstLabel: string
  secondKey?: 'shippedQuantity'
  secondLabel?: string
  ariaLabel: string
}) {
  const width = 680
  const height = 210
  const values = data.flatMap((item) => [Number(item[firstKey] ?? 0), ...(secondKey ? [Number(item[secondKey] ?? 0)] : [])])
  const maxValue = Math.max(1, ...values)
  const groupWidth = data.length ? 620 / data.length : 620
  const barWidth = Math.max(3, Math.min(18, secondKey ? groupWidth / 3 : groupWidth / 2))
  const ticks = data.length <= 6 ? data.map((_, index) => index) : Array.from({ length: 6 }, (_, index) => Math.round(index * (data.length - 1) / 5))
  const barHeight = (value: number) => Math.max(0, value / maxValue * 142)
  return <div className={styles.trendWrap}>
    <div className={styles.trendLegend}><span><i className={styles.primaryLegend} />{firstLabel}</span>{secondKey ? <span><i className={styles.secondaryLegend} />{secondLabel}</span> : null}</div>
    <svg role="img" tabIndex={0} aria-label={ariaLabel} viewBox={`0 0 ${width} ${height}`} className={styles.trendChart}>
      <line x1="42" x2="662" y1="164" y2="164" stroke="#aab4c2" strokeDasharray="4 4" />
      {data.map((item, index) => {
        const x = 42 + index * groupWidth + groupWidth / 2
        const first = Number(item[firstKey] ?? 0)
        const second = Number(secondKey ? item[secondKey] ?? 0 : 0)
        return <g key={item.date}>
          <rect x={x - (secondKey ? barWidth + 2 : barWidth / 2)} y={164 - barHeight(first)} width={barWidth} height={barHeight(first)} fill="#246bfd" rx="1"><title>{`${item.date} ${firstLabel} ${first}`}</title></rect>
          {secondKey ? <rect x={x + 2} y={164 - barHeight(second)} width={barWidth} height={barHeight(second)} fill="#14b8a6" rx="1"><title>{`${item.date} ${secondLabel} ${second}`}</title></rect> : null}
        </g>
      })}
      {ticks.map((index) => <text key={data[index]?.date} x={42 + index * groupWidth + groupWidth / 2} y="192" textAnchor="middle" fontSize="11" fill="#6b7280">{data[index]?.date.slice(5)}</text>)}
    </svg>
  </div>
}

export default function WarehouseProductFlowAnalysisPage() {
  const { t } = useTranslation()
  // 货柜编号默认只显示后 4 位；有权限的账号点列头眼睛按钮显示完整编号。
  const { format: formatContainerNumber } = useContainerNumberDisplay()
  const defaultPeriods = useMemo(() => buildWarehouseProductFlowDefaultPeriods(), [])
  const [periods, setPeriods] = useState<WarehouseProductFlowPeriods>(defaultPeriods)
  const [containerRange, setContainerRange] = useState<[Dayjs, Dayjs]>(() => toRange(defaultPeriods.containerPeriod))
  const [orderShipmentRange, setOrderShipmentRange] = useState<[Dayjs, Dayjs]>(() => toRange(defaultPeriods.orderShipmentPeriod))
  const [salesRange, setSalesRange] = useState<[Dayjs, Dayjs]>(() => toRange(defaultPeriods.salesPeriod))
  const [draftKeyword, setDraftKeyword] = useState('')
  const [draftCategories, setDraftCategories] = useState<string[]>([])
  const [categorySearchText, setCategorySearchText] = useState('')
  const [draftSuppliers, setDraftSuppliers] = useState<string[]>([])
  const [supplierSearchText, setSupplierSearchText] = useState('')
  const [draftDocument, setDraftDocument] = useState('')
  const [filter, setFilter] = useState<WarehouseProductFlowFilter>(() => createWarehouseProductFlowFilter('', [], [], ''))
  const [selection, setSelection] = useState<WarehouseProductFlowSelection>(createIncludedSelection())
  const [currentProductCode, setCurrentProductCode] = useState<string | null>(null)
  const [currentProductSnapshot, setCurrentProductSnapshot] = useState<WarehouseProductFlowCandidate | null>(null)
  const [candidatePage, setCandidatePage] = useState(1)
  const [globalRefreshVersion, setGlobalRefreshVersion] = useState(0)
  const [containerRefreshVersion, setContainerRefreshVersion] = useState(0)
  const [orderShipmentRefreshVersion, setOrderShipmentRefreshVersion] = useState(0)
  const [salesRefreshVersion, setSalesRefreshVersion] = useState(0)
  const [optionsRefreshVersion, setOptionsRefreshVersion] = useState(0)
  const [candidates, setCandidates] = useState<WarehouseProductFlowCandidatesData | null>(null)
  const [candidateLoading, setCandidateLoading] = useState(true)
  const [candidateError, setCandidateError] = useState<LocalizedError>()
  const [summary, setSummary] = useState<WarehouseProductFlowSummaryData | null>(null)
  const [summaryLoading, setSummaryLoading] = useState(true)
  const [summaryError, setSummaryError] = useState<LocalizedError>()
  const [categoryTree, setCategoryTree] = useState<WarehouseCategoryNode[]>([])
  const [categoryError, setCategoryError] = useState<LocalizedError>()
  const [supplierOptions, setSupplierOptions] = useState<Array<{ code: string; name?: string }>>([])
  const [activeTab, setActiveTab] = useState<'container' | 'order' | 'shipment'>('container')
  const [containerRows, setContainerRows] = useState<Array<Record<string, unknown>>>([])
  const [orderRows, setOrderRows] = useState<Array<Record<string, unknown>>>([])
  const [shipmentRows, setShipmentRows] = useState<Array<WarehouseProductFlowShipmentRow & Record<string, unknown>>>([])
  const [shipmentPosFilter, setShipmentPosFilter] = useState<WarehouseProductFlowPosFilter>('all')
  const [containerState, setContainerState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [orderState, setOrderState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [shipmentState, setShipmentState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [orderShipmentDaily, setOrderShipmentDaily] = useState<WarehouseProductFlowDaily[]>([])
  const [containerDaily, setContainerDaily] = useState<WarehouseProductFlowDaily[]>([])
  const [containerTrendState, setContainerTrendState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [orderShipmentTrendState, setOrderShipmentTrendState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [salesDaily, setSalesDaily] = useState<WarehouseProductFlowDaily[]>([])
  const [salesTrendState, setSalesTrendState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [branches, setBranches] = useState<WarehouseProductFlowBranch[]>([])
  const [branchState, setBranchState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })
  const [branchCode, setBranchCode] = useState<string | null>(null)
  const [branchDaily, setBranchDaily] = useState<WarehouseProductFlowDaily[]>([])
  const [branchDailyState, setBranchDailyState] = useState<{ loading: boolean; error?: LocalizedError }>({ loading: false })

  const candidateGuard = useRef(createLatestRequestGuard())
  const summaryGuard = useRef(createLatestRequestGuard())
  const containerGuard = useRef(createLatestRequestGuard())
  const orderGuard = useRef(createLatestRequestGuard())
  const shipmentGuard = useRef(createLatestRequestGuard())
  const orderShipmentTrendGuard = useRef(createLatestRequestGuard())
  const containerTrendGuard = useRef(createLatestRequestGuard())
  const salesTrendGuard = useRef(createLatestRequestGuard())
  const branchGuard = useRef(createLatestRequestGuard())
  const branchDailyGuard = useRef(createLatestRequestGuard())
  const shouldSelectFirstCandidate = useRef(true)
  const forceRefreshPending = useRef(new Set<string>())

  const periodsRef = useRef(periods)
  periodsRef.current = periods
  const yesterday = defaultPeriods.salesPeriod.endDate
  const categoryOptions = useMemo(() => buildWarehouseProductFlowCategoryOptions(categoryTree), [categoryTree])
  const visibleCategoryOptions = useMemo(() => filterWarehouseProductFlowCategoryOptions(categoryOptions, categorySearchText), [categoryOptions, categorySearchText])
  const visibleSupplierOptions = useMemo(() => filterWarehouseProductFlowSupplierOptions(supplierOptions, supplierSearchText), [supplierOptions, supplierSearchText])
  const visibleShipmentRows = useMemo(() => filterWarehouseProductFlowShipments(shipmentRows, shipmentPosFilter), [shipmentRows, shipmentPosFilter])
  const sortedBranches = useMemo(() => sortWarehouseProductFlowBranches(branches), [branches])
  // 候选顺序以服务端稳定分页为准，前端不得只对当前页再次排序。
  const candidateItems = candidates?.items || []
  const selectedRows = summary?.items || []
  const currentSummaryProduct = summary?.currentProduct?.productCode === currentProductCode
    ? summary.currentProduct
    : selectedRows.find((item) => item.productCode === currentProductCode)
  const validSnapshot = currentProductSnapshot?.productCode === currentProductCode ? currentProductSnapshot : null
  const currentProduct = currentSummaryProduct ?? validSnapshot ?? candidateItems.find((item) => item.productCode === currentProductCode)
  const currentMetrics = currentSummaryProduct?.metrics ?? emptyMetrics()
  const summaryTotals = summary?.totals ?? emptyMetrics()
  const selectedLabel = selection.mode === 'allFiltered'
    ? t('warehouseProductFlowAnalysis.selection.allFiltered', { count: selection.excludedProductCodes.length })
    : t('warehouseProductFlowAnalysis.selection.selected', { count: selection.includedProductCodes.length })

  const consumeForceRefresh = useCallback((key: string) => {
    if (!forceRefreshPending.current.has(key)) return false
    forceRefreshPending.current.delete(key)
    return true
  }, [])

  const productRequest = useCallback(() => ({ filter, periods: periodsRef.current, currentProductCode: currentProductCode! }), [currentProductCode, filter])

  useEffect(() => {
    let active = true
    getCategoryTree().then((tree) => { if (active) setCategoryTree(tree) }).catch((error) => { if (active) setCategoryError(localizedError(error, 'warehouseProductFlowAnalysis.errors.categories')) })
    return () => { active = false }
  }, [optionsRefreshVersion])

  useEffect(() => {
    let active = true
    // options 只在页面初始化和显式刷新时获取，避免输入、翻页或选择触发重复请求。
    getWarehouseProductFlowOptions(undefined, undefined, optionsRefreshVersion > 0).then((result) => { if (active) setSupplierOptions(result.data.domesticSuppliers) }).catch(() => { if (active) setSupplierOptions([]) })
    return () => { active = false }
  }, [optionsRefreshVersion])

  const loadCandidates = useCallback(() => {
    const requestId = candidateGuard.current.begin()
    setCandidateLoading(true); setCandidateError(undefined)
    queryWarehouseProductFlowCandidates({ filter, pageNumber: candidatePage, pageSize: 20, sortBy: 'itemNumber', sortDirection: 'asc', forceRefresh: consumeForceRefresh('candidates') })
      .then((result) => {
        if (!candidateGuard.current.isLatest(requestId)) return
        setCandidates(result.data)
        if (shouldSelectFirstCandidate.current) {
          shouldSelectFirstCandidate.current = false
          const firstSelection = selectFirstCandidate(createIncludedSelection(), result.data.items)
          setSelection(firstSelection)
          setCurrentProductCode(firstSelection.includedProductCodes[0] ?? null)
          setCurrentProductSnapshot(result.data.items[0] ?? null)
        }
      }).catch((error) => { if (candidateGuard.current.isLatest(requestId) && !isAbortError(error)) setCandidateError(localizedError(error, 'warehouseProductFlowAnalysis.errors.candidates')) })
      .finally(() => { if (candidateGuard.current.isLatest(requestId)) setCandidateLoading(false) })
  }, [candidatePage, consumeForceRefresh, filter, globalRefreshVersion])

  useEffect(() => { loadCandidates(); return () => candidateGuard.current.invalidate() }, [loadCandidates])

  useEffect(() => {
    if (selection.mode === 'included' && !selection.includedProductCodes.length) {
      setSummary(null); setSummaryLoading(false); return
    }
    const requestId = summaryGuard.current.begin()
    setSummaryLoading(true); setSummaryError(undefined)
    queryWarehouseProductFlowSummary({ filter, periods, selection, currentProductCode: currentProductCode ?? undefined, pageNumber: 1, pageSize: 20, sortBy: 'itemNumber', sortDirection: 'asc', forceRefresh: consumeForceRefresh('summary') })
      .then((result) => { if (summaryGuard.current.isLatest(requestId)) setSummary(result.data) })
      .catch((error) => { if (summaryGuard.current.isLatest(requestId) && !isAbortError(error)) setSummaryError(localizedError(error, 'warehouseProductFlowAnalysis.errors.summary')) })
      .finally(() => { if (summaryGuard.current.isLatest(requestId)) setSummaryLoading(false) })
    return () => summaryGuard.current.invalidate()
  }, [consumeForceRefresh, containerRefreshVersion, currentProductCode, filter, globalRefreshVersion, orderShipmentRefreshVersion, periods, salesRefreshVersion, selection])

  useEffect(() => {
    if (currentProductCode) return
    setContainerRows([]); setContainerDaily([]); setOrderRows([]); setShipmentRows([]); setOrderShipmentDaily([]); setSalesDaily([]); setBranches([])
    setBranchCode(null); setBranchDaily([])
  }, [currentProductCode])

  // 各区块只订阅所需日期；发货明细额外订阅销售期间，使新增销量列与右侧分店销售保持一致。
  useEffect(() => {
    if (!currentProductCode) return
    const requestId = containerGuard.current.begin(); setContainerState({ loading: true })
    queryWarehouseProductFlowContainers({ ...productRequest(), forceRefresh: consumeForceRefresh('containers') })
      .then((result) => { if (containerGuard.current.isLatest(requestId)) setContainerRows(result.data.map((item) => ({ ...item }))) })
      .catch((error) => { if (containerGuard.current.isLatest(requestId) && !isAbortError(error)) setContainerState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.containerDetails') }) })
      .finally(() => { if (containerGuard.current.isLatest(requestId)) setContainerState((state) => ({ ...state, loading: false })) })
    return () => containerGuard.current.invalidate()
  }, [consumeForceRefresh, containerRefreshVersion, currentProductCode, filter, globalRefreshVersion, periods.containerPeriod.endDate, periods.containerPeriod.startDate, productRequest])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = containerTrendGuard.current.begin(); setContainerTrendState({ loading: true })
    queryWarehouseProductFlowDaily({ ...productRequest(), forceRefresh: consumeForceRefresh('productDaily') })
      .then((result) => { if (containerTrendGuard.current.isLatest(requestId)) setContainerDaily(result.data) })
      .catch((error) => { if (containerTrendGuard.current.isLatest(requestId) && !isAbortError(error)) setContainerTrendState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.containerTrend') }) })
      .finally(() => { if (containerTrendGuard.current.isLatest(requestId)) setContainerTrendState((state) => ({ ...state, loading: false })) })
    return () => containerTrendGuard.current.invalidate()
  }, [consumeForceRefresh, containerRefreshVersion, currentProductCode, filter, globalRefreshVersion, periods.containerPeriod.endDate, periods.containerPeriod.startDate, productRequest])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = orderGuard.current.begin(); setOrderState({ loading: true })
    queryWarehouseProductFlowOrders({ ...productRequest(), forceRefresh: consumeForceRefresh('orders') })
      .then((result) => { if (orderGuard.current.isLatest(requestId)) setOrderRows(result.data.map((item) => ({ ...item }))) })
      .catch((error) => { if (orderGuard.current.isLatest(requestId) && !isAbortError(error)) setOrderState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.orderDetails') }) })
      .finally(() => { if (orderGuard.current.isLatest(requestId)) setOrderState((state) => ({ ...state, loading: false })) })
    return () => orderGuard.current.invalidate()
  }, [consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, orderShipmentRefreshVersion, periods.orderShipmentPeriod.endDate, periods.orderShipmentPeriod.startDate, productRequest])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = shipmentGuard.current.begin(); setShipmentState({ loading: true })
    queryWarehouseProductFlowShipments({ ...productRequest(), forceRefresh: consumeForceRefresh('shipments') })
      .then((result) => { if (shipmentGuard.current.isLatest(requestId)) setShipmentRows(result.data.map((item) => ({ ...item }))) })
      .catch((error) => { if (shipmentGuard.current.isLatest(requestId) && !isAbortError(error)) setShipmentState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.shipmentDetails') }) })
      .finally(() => { if (shipmentGuard.current.isLatest(requestId)) setShipmentState((state) => ({ ...state, loading: false })) })
    return () => shipmentGuard.current.invalidate()
  }, [consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, orderShipmentRefreshVersion, periods.orderShipmentPeriod.endDate, periods.orderShipmentPeriod.startDate, periods.salesPeriod.endDate, periods.salesPeriod.startDate, productRequest, salesRefreshVersion])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = orderShipmentTrendGuard.current.begin(); setOrderShipmentTrendState({ loading: true })
    queryWarehouseProductFlowOrderShipmentDaily({ ...productRequest(), forceRefresh: consumeForceRefresh('orderShipmentDaily') })
      .then((result) => { if (orderShipmentTrendGuard.current.isLatest(requestId)) setOrderShipmentDaily(result.data) })
      .catch((error) => { if (orderShipmentTrendGuard.current.isLatest(requestId) && !isAbortError(error)) setOrderShipmentTrendState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.orderShipmentTrend') }) })
      .finally(() => { if (orderShipmentTrendGuard.current.isLatest(requestId)) setOrderShipmentTrendState((state) => ({ ...state, loading: false })) })
    return () => orderShipmentTrendGuard.current.invalidate()
  }, [consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, orderShipmentRefreshVersion, periods.orderShipmentPeriod.endDate, periods.orderShipmentPeriod.startDate, productRequest])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = salesTrendGuard.current.begin(); setSalesTrendState({ loading: true })
    queryWarehouseProductFlowSalesDaily({ ...productRequest(), forceRefresh: consumeForceRefresh('salesDaily') })
      .then((result) => { if (salesTrendGuard.current.isLatest(requestId)) setSalesDaily(result.data) })
      .catch((error) => { if (salesTrendGuard.current.isLatest(requestId) && !isAbortError(error)) setSalesTrendState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.salesTrend') }) })
      .finally(() => { if (salesTrendGuard.current.isLatest(requestId)) setSalesTrendState((state) => ({ ...state, loading: false })) })
    return () => salesTrendGuard.current.invalidate()
  }, [consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, periods.salesPeriod.endDate, periods.salesPeriod.startDate, productRequest, salesRefreshVersion])

  useEffect(() => {
    if (!currentProductCode) return
    const requestId = branchGuard.current.begin(); setBranchState({ loading: true })
    queryWarehouseProductFlowBranches({ ...productRequest(), forceRefresh: consumeForceRefresh('branches') })
      .then((result) => { if (branchGuard.current.isLatest(requestId)) setBranches(result.data) })
      .catch((error) => { if (branchGuard.current.isLatest(requestId) && !isAbortError(error)) setBranchState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.branchSales') }) })
      .finally(() => { if (branchGuard.current.isLatest(requestId)) setBranchState((state) => ({ ...state, loading: false })) })
    return () => branchGuard.current.invalidate()
  }, [consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, periods.salesPeriod.endDate, periods.salesPeriod.startDate, productRequest, salesRefreshVersion])

  useEffect(() => {
    branchDailyGuard.current.invalidate(); setBranchCode(null); setBranchDaily([]); setBranchDailyState({ loading: false })
  }, [currentProductCode])

  useEffect(() => {
    if (!currentProductCode || !branchCode) return
    const requestId = branchDailyGuard.current.begin(); setBranchDailyState({ loading: true })
    queryWarehouseProductFlowBranchDaily({ ...productRequest(), branchCode, forceRefresh: consumeForceRefresh('branchDaily') })
      .then((result) => { if (branchDailyGuard.current.isLatest(requestId)) setBranchDaily(result.data) })
      .catch((error) => { if (branchDailyGuard.current.isLatest(requestId) && !isAbortError(error)) setBranchDailyState({ loading: false, error: localizedError(error, 'warehouseProductFlowAnalysis.errors.branchTrend') }) })
      .finally(() => { if (branchDailyGuard.current.isLatest(requestId)) setBranchDailyState((state) => ({ ...state, loading: false })) })
    return () => branchDailyGuard.current.invalidate()
  }, [branchCode, consumeForceRefresh, currentProductCode, filter, globalRefreshVersion, periods.salesPeriod.endDate, periods.salesPeriod.startDate, productRequest, salesRefreshVersion])

  const submitMasterFilter = () => {
    candidateGuard.current.invalidate(); shouldSelectFirstCandidate.current = true
    setSelection(createIncludedSelection()); setCurrentProductCode(null); setCurrentProductSnapshot(null); setCandidates(null); setSummary(null); setCandidatePage(1)
    setFilter(createWarehouseProductFlowFilter(draftKeyword, draftCategories, draftSuppliers, draftDocument))
  }

  const resetFilters = () => {
    setDraftKeyword(''); setDraftCategories([]); setDraftSuppliers([]); setDraftDocument('')
    setShipmentPosFilter('all')
    setContainerRange(toRange(defaultPeriods.containerPeriod)); setOrderShipmentRange(toRange(defaultPeriods.orderShipmentPeriod)); setSalesRange(toRange(defaultPeriods.salesPeriod))
    setPeriods(defaultPeriods)
    setContainerRefreshVersion((value) => value + 1); setOrderShipmentRefreshVersion((value) => value + 1); setSalesRefreshVersion((value) => value + 1)
    shouldSelectFirstCandidate.current = true; setSelection(createIncludedSelection()); setCurrentProductCode(null); setCurrentProductSnapshot(null); setCandidatePage(1)
    setFilter(createWarehouseProductFlowFilter('', [], [], ''))
  }

  const updateRange = (setRange: (range: [Dayjs, Dayjs]) => void, value: null | [Dayjs | null, Dayjs | null]) => {
    if (!value?.[0] || !value?.[1]) return
    setRange([value[0], value[1]])
  }

  const applyPeriod = (key: keyof WarehouseProductFlowPeriods, range: [Dayjs, Dayjs], refresh: Dispatch<SetStateAction<number>>) => {
    if (!isValidWarehouseProductFlowRange([range[0].toDate(), range[1].toDate()])) {
      message.warning(t('warehouseProductFlowAnalysis.validation.invalidDateRange'))
      return
    }
    setPeriods((current) => ({ ...current, [key]: toPeriod(range) }))
    refresh((value) => value + 1)
  }

  const restorePeriod = (key: keyof WarehouseProductFlowPeriods, setRange: (range: [Dayjs, Dayjs]) => void, refresh: Dispatch<SetStateAction<number>>) => {
    const defaultRange = toRange(defaultPeriods[key])
    setRange(defaultRange)
    setPeriods((current) => ({ ...current, [key]: defaultPeriods[key] }))
    refresh((value) => value + 1)
  }

  const toggleCandidate = (product: WarehouseProductFlowCandidate, checked: boolean) => {
    const next = toggleProductSelection(selection, product.productCode, checked)
    setSelection(next)
    if (checked) { setCurrentProductCode(product.productCode); setCurrentProductSnapshot(product) }
    else {
      const fallbackProducts = [...selectedRows, ...candidateItems].filter((item, index, rows) => rows.findIndex((row) => row.productCode === item.productCode) === index)
      const nextCurrentCode = resolveCurrentProductCode(currentProductCode, fallbackProducts.filter((item) => isProductSelected(next, item.productCode)).map((item) => item.productCode))
      setCurrentProductCode(nextCurrentCode)
      setCurrentProductSnapshot(fallbackProducts.find((item) => item.productCode === nextCurrentCode) ?? null)
    }
  }

  const containerColumns: ColumnsType<Record<string, unknown>> = [
    {
      title: (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
          {t('warehouseProductFlowAnalysis.tables.containerNumber')}
          <ContainerNumberRevealButton />
        </span>
      ),
      dataIndex: 'containerNumber',
      render: (value) => (typeof value === 'string' ? formatContainerNumber(value) : value),
    }, { title: t('warehouseProductFlowAnalysis.tables.arrivalDate'), dataIndex: 'arrivalDate' }, { title: t('warehouseProductFlowAnalysis.tables.quantity'), dataIndex: 'inboundQuantity', align: 'right' }, { title: t('warehouseProductFlowAnalysis.tables.unitPrice'), dataIndex: 'inboundUnitPrice', align: 'right', render: (value) => typeof value === 'number' ? moneyFormatter.format(value) : '—' }, { title: t('warehouseProductFlowAnalysis.tables.supplier'), dataIndex: 'supplierName' },
  ]
  const orderColumns: ColumnsType<Record<string, unknown>> = [
    { title: t('warehouseProductFlowAnalysis.tables.orderNumber'), dataIndex: 'orderNumber' }, { title: t('warehouseProductFlowAnalysis.tables.branch'), dataIndex: 'branchName' }, { title: t('warehouseProductFlowAnalysis.tables.orderDate'), dataIndex: 'orderDate' }, { title: t('warehouseProductFlowAnalysis.tables.orderedQuantity'), dataIndex: 'orderedQuantity', align: 'right' },
  ]
  const shipmentColumns: ColumnsType<Record<string, unknown>> = [
    { title: t('warehouseProductFlowAnalysis.tables.shipmentOrOrderNumber'), key: 'number', render: (_, row) => row.shipmentNumber || row.orderNumber || '—' },
    { title: t('warehouseProductFlowAnalysis.tables.branch'), dataIndex: 'branchName', render: (_, row) => row.branchName || row.branchCode || '—' },
    { title: t('warehouseProductFlowAnalysis.tables.shipmentDate'), dataIndex: 'shipmentDate' },
    { title: t('warehouseProductFlowAnalysis.tables.shippedQuantity'), dataIndex: 'shippedQuantity', align: 'right', fixed: 'right', width: 90 },
    { title: t('warehouseProductFlowAnalysis.tables.salesQuantity'), dataIndex: 'netSalesQuantity', align: 'right', fixed: 'right', width: 90, render: (value) => typeof value === 'number' ? numberFormatter.format(value) : '—' },
  ]
  const detailData = activeTab === 'container' ? containerRows : activeTab === 'order' ? orderRows : visibleShipmentRows
  const detailState = activeTab === 'container' ? containerState : activeTab === 'order' ? orderState : shipmentState
  const detailColumns = activeTab === 'container' ? containerColumns : activeTab === 'order' ? orderColumns : shipmentColumns
  const branchColumns: ColumnsType<WarehouseProductFlowBranch> = [
    { title: t('warehouseProductFlowAnalysis.tables.branch'), dataIndex: 'branchName', render: (_, row) => <button type="button" className={styles.branchButton} onClick={() => setBranchCode(row.branchCode)}>{row.branchName || row.branchCode}</button> },
    { title: t('warehouseProductFlowAnalysis.tables.netSalesQuantity'), dataIndex: 'netSalesQuantity', align: 'right' }, { title: t('warehouseProductFlowAnalysis.tables.netSalesAmount'), dataIndex: 'netSalesAmount', align: 'right', render: (value) => moneyFormatter.format(Number(value)) }, { title: t('warehouseProductFlowAnalysis.tables.averagePrice'), dataIndex: 'averageUnitPrice', align: 'right', render: (value) => value === null ? '—' : moneyFormatter.format(Number(value)) },
  ]

  const activeDetailRange = activeTab === 'container' ? containerRange : orderShipmentRange
  const setActiveDetailRange = (value: null | [Dayjs | null, Dayjs | null]) => updateRange(activeTab === 'container' ? setContainerRange : setOrderShipmentRange, value)
  const queryActiveDetail = () => activeTab === 'container'
    ? applyPeriod('containerPeriod', containerRange, setContainerRefreshVersion)
    : applyPeriod('orderShipmentPeriod', orderShipmentRange, setOrderShipmentRefreshVersion)
  const restoreActiveDetail = () => {
    if (activeTab === 'shipment') setShipmentPosFilter('all')
    if (activeTab === 'container') restorePeriod('containerPeriod', setContainerRange, setContainerRefreshVersion)
    else restorePeriod('orderShipmentPeriod', setOrderShipmentRange, setOrderShipmentRefreshVersion)
  }
  const retryActiveDetail = () => activeTab === 'container'
    ? setContainerRefreshVersion((value) => value + 1)
    : setOrderShipmentRefreshVersion((value) => value + 1)

  return <PageContainer title={t('warehouseProductFlowAnalysis.title')} subtitle={t('warehouseProductFlowAnalysis.subtitle')}>
    <div className={styles.page}>
      <section className={styles.toolbar}>
        <Button type="primary" icon={<SearchOutlined />} onClick={submitMasterFilter}>{t('warehouseProductFlowAnalysis.actions.queryProducts')}</Button><Button onClick={resetFilters}>{t('common.reset')}</Button>
        <Button icon={<ReloadOutlined />} onClick={() => { forceRefreshPending.current = new Set(['candidates', 'summary', 'containers', 'productDaily', 'orders', 'shipments', 'orderShipmentDaily', 'salesDaily', 'branches', 'branchDaily']); setOptionsRefreshVersion((value) => value + 1); setGlobalRefreshVersion((value) => value + 1) }}>{t('warehouseProductFlowAnalysis.actions.refreshAll')}</Button>
      </section>
      <section className={styles.stages}>
        <StageCard step={1} label={t('warehouseProductFlowAnalysis.stages.containerInbound')} value={summaryTotals.inboundQuantity} tone={styles.blue} period={periods.containerPeriod} /><span className={styles.arrow}>→</span><StageCard step={2} label={t('warehouseProductFlowAnalysis.stages.branchOrders')} value={summaryTotals.orderedQuantity ?? 0} tone={styles.indigo} period={periods.orderShipmentPeriod} /><span className={styles.arrow}>→</span><StageCard step={3} label={t('warehouseProductFlowAnalysis.stages.shippedToBranches')} value={summaryTotals.shippedQuantity} tone={styles.teal} period={periods.orderShipmentPeriod} /><span className={styles.arrow}>→</span><StageCard step={4} label={t('warehouseProductFlowAnalysis.stages.branchNetSales')} value={summaryTotals.netSalesQuantity} tone={styles.orange} period={periods.salesPeriod} />
      </section>
      {summaryLoading ? <Spin size="small" /> : summaryError ? <Alert type="error" showIcon message={t('warehouseProductFlowAnalysis.errors.summaryTitle')} description={<LocalizedErrorDescription error={summaryError} />} /> : null}

      <main className={styles.layout}>
        <aside className={styles.panel}>
          <h3>{t('warehouseProductFlowAnalysis.master.title')}</h3>
          <div className={styles.filters}>
            {categoryError ? <Alert type="warning" showIcon message={t('warehouseProductFlowAnalysis.errors.categoriesTitle')} description={<LocalizedErrorDescription error={categoryError} />} /> : null}
            <label>{t('warehouseProductFlowAnalysis.filters.category')}<Select mode="multiple" showSearch filterOption={false} value={draftCategories} options={visibleCategoryOptions} onSearch={setCategorySearchText} onOpenChange={(open) => { if (!open) setCategorySearchText('') }} onChange={setDraftCategories} placeholder={t('warehouseProductFlowAnalysis.filters.categoryPlaceholder')} /></label>
            <label>{t('warehouseProductFlowAnalysis.filters.keyword')}<Input value={draftKeyword} onChange={(event) => setDraftKeyword(event.target.value)} placeholder={t('warehouseProductFlowAnalysis.filters.keywordPlaceholder')} onPressEnter={submitMasterFilter} /></label>
            <label>{t('warehouseProductFlowAnalysis.filters.supplier')}<Select mode="multiple" showSearch filterOption={false} value={draftSuppliers} options={visibleSupplierOptions} onSearch={setSupplierSearchText} onOpenChange={(open) => { if (!open) setSupplierSearchText('') }} onChange={setDraftSuppliers} placeholder={t('warehouseProductFlowAnalysis.filters.supplierPlaceholder')} /></label>
            <label>{t('warehouseProductFlowAnalysis.filters.containerNumber')}<Input value={draftDocument} onChange={(event) => setDraftDocument(event.target.value)} placeholder={t('warehouseProductFlowAnalysis.filters.containerNumberPlaceholder')} onPressEnter={submitMasterFilter} /></label>
          </div>
          <div className={styles.selectionBar}><span>{selectedLabel}</span><span><Button type="link" size="small" onClick={() => setSelection(createAllFilteredSelection())}>{t('warehouseProductFlowAnalysis.actions.selectAllFiltered')}</Button><Button type="link" size="small" onClick={() => { setSelection(createIncludedSelection()); setCurrentProductCode(null); setCurrentProductSnapshot(null) }}>{t('common.clearSelection')}</Button></span></div>
          <LocalPanel loading={candidateLoading} error={candidateError} retry={loadCandidates}>
            {candidateItems.length ? <div className={styles.candidateList}>{candidateItems.map((product) => <div key={product.productCode} className={`${styles.candidate} ${currentProductCode === product.productCode ? styles.currentCandidate : ''}`}>
              <Checkbox checked={isProductSelected(selection, product.productCode)} onClick={(event) => event.stopPropagation()} onChange={(event) => toggleCandidate(product, event.target.checked)} />
              <button type="button" className={styles.candidateMain} disabled={!isProductSelected(selection, product.productCode)} onClick={() => { setCurrentProductCode(product.productCode); setCurrentProductSnapshot(product) }}><ProductImage src={product.imageUrl} alt={product.productName || product.productCode} size={48} /><span className={styles.candidateText}><strong>{t('warehouseProductFlowAnalysis.product.itemNumber')}: {product.itemNumber || '—'}</strong><span>{product.productName || product.englishName || t('warehouseProductFlowAnalysis.product.unnamed')}</span><small>{t('warehouseProductFlowAnalysis.product.code')}: {product.productCode}</small><small>{t('warehouseProductFlowAnalysis.product.barcode')}: {product.barcode || '—'}</small><small>{product.categoryName || t('warehouseProductFlowAnalysis.product.uncategorized')} · {product.supplierName || t('warehouseProductFlowAnalysis.product.supplierUnmapped')}</small></span></button>
            </div>)}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('warehouseProductFlowAnalysis.empty.noProducts')} />}
          </LocalPanel>
          <Pagination size="small" current={candidatePage} total={candidates?.total || 0} pageSize={20} showSizeChanger={false} onChange={setCandidatePage} />
        </aside>

        <section className={styles.centerColumn}>
          <div className={styles.panel}>
            {currentProduct ? <div className={styles.productHeader}><ProductImage src={currentProduct.imageUrl} alt={currentProduct.productName || currentProduct.productCode} size={64} /><div><strong>{t('warehouseProductFlowAnalysis.product.itemNumber')}: {currentProduct.itemNumber || '—'}</strong><h3>{currentProduct.productName || currentProduct.englishName || t('warehouseProductFlowAnalysis.product.unnamed')}</h3><span>{t('warehouseProductFlowAnalysis.product.code')}: {currentProduct.productCode} · {t('warehouseProductFlowAnalysis.product.barcode')}: {currentProduct.barcode || '—'}</span></div></div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('warehouseProductFlowAnalysis.empty.selectProduct')} />}
            <div className={styles.kpis}>{[[t('warehouseProductFlowAnalysis.metrics.containerInbound'), currentMetrics.inboundQuantity], [t('warehouseProductFlowAnalysis.metrics.branchOrders'), currentMetrics.orderedQuantity ?? 0], [t('warehouseProductFlowAnalysis.metrics.shippedToBranches'), currentMetrics.shippedQuantity], [t('warehouseProductFlowAnalysis.metrics.branchNetSales'), currentMetrics.netSalesQuantity]].map(([label, value]) => <div key={String(label)}><span>{label}</span><strong>{numberFormatter.format(Number(value))}</strong></div>)}</div>
            <Tabs activeKey={activeTab} onChange={(key) => setActiveTab(key as 'container' | 'order' | 'shipment')} items={[{ key: 'container', label: t('warehouseProductFlowAnalysis.tabs.containerDetails') }, { key: 'order', label: t('warehouseProductFlowAnalysis.tabs.branchOrders') }, { key: 'shipment', label: t('warehouseProductFlowAnalysis.tabs.shipmentDetails') }]} />
            <div className={styles.periodControls}><RangePicker value={activeDetailRange} disabledDate={(date) => date.isAfter(yesterday, 'day')} onChange={setActiveDetailRange} allowClear={false} /><Button type="primary" size="small" onClick={queryActiveDetail}>{t('common.query')}</Button><Button size="small" onClick={restoreActiveDetail}>{t('warehouseProductFlowAnalysis.actions.restoreDefault')}</Button></div>
            {activeTab === 'shipment' ? <>
              <div className={styles.shipmentFilters}>
                <label htmlFor="shipment-pos-filter">{t('warehouseProductFlowAnalysis.shipments.posLabel')}</label>
                <Select id="shipment-pos-filter" aria-label={t('warehouseProductFlowAnalysis.shipments.posAria')} size="small" value={shipmentPosFilter} onChange={setShipmentPosFilter} options={[{ value: 'all', label: t('warehouseProductFlowAnalysis.shipments.allBranches') }, { value: 'enabled', label: t('warehouseProductFlowAnalysis.shipments.posEnabled') }, { value: 'disabled', label: t('warehouseProductFlowAnalysis.shipments.posDisabled') }]} />
              </div>
              <p className={styles.salesPeriodNote}>{t('warehouseProductFlowAnalysis.shipments.salesPeriodNote', { period: formatPeriodLabel(periods.salesPeriod) })}</p>
            </> : null}
            <LocalPanel loading={detailState.loading} error={detailState.error} retry={retryActiveDetail}><MeasuredTable metricId="executive-sales-intelligence.warehouse-product-flow-analysis.table-1" rowKey={(_, index) => `${activeTab}-${index}`} columns={detailColumns} dataSource={detailData} size="small" pagination={false} scroll={{ x: activeTab === 'shipment' ? 600 : 520 }} /></LocalPanel>
            <h3 className={styles.chartTitle}>{t('warehouseProductFlowAnalysis.charts.containerTrend')} <small>{formatPeriodLabel(periods.containerPeriod)}</small></h3><LocalPanel loading={containerTrendState.loading} error={containerTrendState.error} retry={() => setContainerRefreshVersion((value) => value + 1)}><QuantityTrend data={containerDaily} firstKey="inboundQuantity" firstLabel={t('warehouseProductFlowAnalysis.charts.inboundQuantity')} ariaLabel={t('warehouseProductFlowAnalysis.charts.containerTrendAria')} /></LocalPanel>
            <h3 className={styles.chartTitle}>{t('warehouseProductFlowAnalysis.charts.orderShipmentTrend')} <small>{formatPeriodLabel(periods.orderShipmentPeriod)}</small></h3><LocalPanel loading={orderShipmentTrendState.loading} error={orderShipmentTrendState.error} retry={() => setOrderShipmentRefreshVersion((value) => value + 1)}><QuantityTrend data={orderShipmentDaily} firstKey="orderedQuantity" firstLabel={t('warehouseProductFlowAnalysis.charts.orderedQuantity')} secondKey="shippedQuantity" secondLabel={t('warehouseProductFlowAnalysis.charts.shippedQuantity')} ariaLabel={t('warehouseProductFlowAnalysis.charts.orderShipmentTrendAria')} /></LocalPanel>
          </div>
        </section>

        <aside className={`${styles.panel} ${styles.rightColumn}`}>
          <div className={styles.sectionHead}><h3>{t('warehouseProductFlowAnalysis.branchSales.title')}</h3></div>
          <div className={styles.periodControls}><RangePicker value={salesRange} disabledDate={(date) => date.isAfter(yesterday, 'day')} onChange={(value) => updateRange(setSalesRange, value)} allowClear={false} /><Button type="primary" size="small" onClick={() => applyPeriod('salesPeriod', salesRange, setSalesRefreshVersion)}>{t('common.query')}</Button><Button size="small" onClick={() => restorePeriod('salesPeriod', setSalesRange, setSalesRefreshVersion)}>{t('warehouseProductFlowAnalysis.actions.restoreDefault')}</Button></div>
          <LocalPanel loading={branchState.loading} error={branchState.error} retry={() => setSalesRefreshVersion((value) => value + 1)}><MeasuredTable metricId="executive-sales-intelligence.warehouse-product-flow-analysis.table-2" rowKey="branchCode" columns={branchColumns} dataSource={sortedBranches} size="small" pagination={false} scroll={{ x: 420 }} onRow={(row) => ({ className: branchCode === row.branchCode ? styles.currentBranch : '' })} /></LocalPanel>
          <h3 className={styles.chartTitle}>{t('warehouseProductFlowAnalysis.charts.salesTrend')} <small>{formatPeriodLabel(periods.salesPeriod)}</small></h3><LocalPanel loading={salesTrendState.loading} error={salesTrendState.error} retry={() => setSalesRefreshVersion((value) => value + 1)}><FlowTrendChart data={salesDaily} ariaLabel={t('warehouseProductFlowAnalysis.charts.salesTrendAria')} /></LocalPanel>
          <h3 className={styles.chartTitle}>{t('warehouseProductFlowAnalysis.charts.branchSalesTrend')}{branchCode ? <Tag color="blue">{branches.find((branch) => branch.branchCode === branchCode)?.branchName || branchCode}</Tag> : null}</h3>
          {branchCode ? <LocalPanel loading={branchDailyState.loading} error={branchDailyState.error} retry={() => setSalesRefreshVersion((value) => value + 1)}><FlowTrendChart data={branchDaily} ariaLabel={t('warehouseProductFlowAnalysis.charts.branchSalesTrendAria')} /></LocalPanel> : <div className={styles.muted}>{t('warehouseProductFlowAnalysis.empty.selectBranch')}</div>}
        </aside>
      </main>
    </div>
  </PageContainer>
}
