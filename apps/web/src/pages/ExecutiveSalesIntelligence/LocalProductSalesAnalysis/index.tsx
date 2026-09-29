import { ClearOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, DatePicker, Empty, Input, Pagination, Segmented, Select, Skeleton, Space, Tree, message } from 'antd'
import type { DataNode } from 'antd/es/tree'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'
import { useCallback, useEffect, useMemo, useRef, useState, type Key, type ReactNode } from 'react'
import PageContainer from '../../../components/PageContainer'
import ProductImage from '../ProductFlowShared/ProductImage'
import {
  getLocalSupplierProductSalesAnalysisOptions,
  getLocalSupplierProductSalesAnalysisSupplierCategoryOptions,
  queryLocalSupplierProductSalesAnalysisBootstrap,
  queryLocalSupplierProductSalesAnalysisBranchDaily,
  queryLocalSupplierProductSalesAnalysisBranches,
  queryLocalSupplierProductSalesAnalysisCandidates,
  queryLocalSupplierProductSalesAnalysisInvoiceDetails,
  queryLocalSupplierProductSalesAnalysisProductDaily,
  queryLocalSupplierProductSalesAnalysisSummary,
} from '../../../services/localSupplierProductSalesAnalysisService'
import type {
  LocalSupplierProductSalesAnalysisBranch,
  LocalSupplierProductSalesAnalysisCandidate,
  LocalSupplierProductSalesAnalysisDaily,
  LocalSupplierProductSalesAnalysisFilter,
  LocalSupplierProductSalesAnalysisInvoiceDetail,
  LocalSupplierProductSalesAnalysisRequest,
  LocalSupplierProductSalesAnalysisSelection,
  LocalSupplierProductSalesAnalysisSummary,
  LocalSupplierProductSalesAnalysisSupplierCategoryOption,
} from '../../../types/localSupplierProductSalesAnalysis'
import {
  applyCandidateSelection,
  applyLocalProductSalesAnalysisBootstrapResult,
  applyLocalProductSalesAnalysisSectionResult,
  buildBranchPriceTiers,
  buildBrisbaneDefaultRange,
  buildLocalProductSalesAnalysisBootstrapRequest,
  canSetCurrentProduct,
  clearLocalProductSalesAnalysisSectionError,
  clearLocalProductSalesAnalysisDetailSections,
  countInclusiveDays,
  createEmptyLocalProductSalesAnalysisState,
  createIncludedSelection,
  createLatestRequestGuard,
  createPageRequestTimeout,
  formatAud,
  getDateRangeError,
  getCurrentProductAfterCancellation,
  collectSupplierCategoryLeafGuids,
  mergeVisibleSupplierCategorySelection,
  getSellThroughLevel,
  isSelected,
  PAGE_BOOTSTRAP_TIMEOUT_SECONDS,
  PAGE_SECTION_TIMEOUT_SECONDS,
  safeDivide,
  setLocalProductSalesAnalysisSectionError,
  type LocalProductSalesAnalysisBootstrapState,
  type LocalProductSalesAnalysisSectionKey,
  type PageRequestTimeout,
  type SellThroughLevel,
  type TrendChartMode,
} from './logic'
import TrendChart from './TrendChart'
import styles from './index.module.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

const { RangePicker } = DatePicker
const quantityFormatter = new Intl.NumberFormat('en-AU')

type BootstrapMode = 'bootstrap' | 'refresh' | 'switch'
interface BootstrapContext { selection?: LocalSupplierProductSalesAnalysisSelection; currentProductCode?: string }

function formatQuantity(value: number) { return quantityFormatter.format(value) }
function errorText(error: unknown, fallback: string) { return error instanceof Error && error.message ? error.message : fallback }
function localErrorDescription(error: string, t: TFunction, language?: string) {
  const validationKeys: Record<string, string> = {
    '参数错误：开始日期不能晚于结束日期': 'localProductSalesAnalysis.errors.dateOrder',
    '参数错误：日期范围截至 Brisbane 昨天': 'localProductSalesAnalysis.errors.futureDate',
    '参数错误：日期范围不能超过 366 天': 'localProductSalesAnalysis.errors.dateLimit',
    '响应格式非法': 'localProductSalesAnalysis.errors.invalidResponse',
    '请求失败': 'localProductSalesAnalysis.errors.load',
  }
  if (validationKeys[error]) return t(validationKeys[error])
  if (error.startsWith('localProductSalesAnalysis.errors.')) return t(error)
  // 保留状态中的原始诊断，展示时随语言翻译；切换语言不会重新发起分析请求。
  if (language?.startsWith('en') && /[\u3400-\u9fff]/u.test(error)) {
    return t(/超时/.test(error) ? 'localProductSalesAnalysis.errors.timeout' : 'localProductSalesAnalysis.errors.load')
  }
  return error
}
function aborted(error: unknown) { return error instanceof Error && error.name === 'AbortError' }
function requestFilter(range: [Dayjs, Dayjs], keyword: string, warehouseCategoryGuids: string[] = [], supplierCodes: string[] = [], supplierCategoryGuids: string[] = [], documentKeyword?: string): LocalSupplierProductSalesAnalysisFilter {
  return {
    startDate: range[0].format('YYYY-MM-DD'), endDate: range[1].format('YYYY-MM-DD'),
    keyword: keyword.trim() || undefined,
    warehouseCategoryGuids: warehouseCategoryGuids.length ? [...warehouseCategoryGuids].sort() : undefined,
    supplierCodes: supplierCodes.length ? [...supplierCodes].sort() : undefined,
    supplierCategoryGuids: supplierCategoryGuids.length ? [...supplierCategoryGuids].sort() : undefined,
    documentKeyword: documentKeyword?.trim() || undefined,
  }
}
function hasSelection(selection: LocalSupplierProductSalesAnalysisSelection) { return selection.mode === 'allFiltered' || selection.includedProductCodes.length > 0 }

function queryAnalysisSection(key: LocalProductSalesAnalysisSectionKey, body: LocalSupplierProductSalesAnalysisRequest, signal: AbortSignal): Promise<{ data: unknown }> {
  if (key === 'options') return getLocalSupplierProductSalesAnalysisOptions(signal)
  if (key === 'summary') return queryLocalSupplierProductSalesAnalysisSummary(body, signal)
  if (key === 'invoiceDetails') return queryLocalSupplierProductSalesAnalysisInvoiceDetails(body, signal)
  if (key === 'productDaily') return queryLocalSupplierProductSalesAnalysisProductDaily(body, signal)
  return queryLocalSupplierProductSalesAnalysisBranches(body, signal)
}

function PanelState({ loading, error, empty, emptyText, retry, children }: { loading: boolean; error?: string; empty?: boolean; emptyText?: string; retry: () => void; children: ReactNode }) {
  const { t, i18n } = useTranslation()
  if (loading) return <div className={styles.state}><Skeleton active title={false} paragraph={{ rows: 3, width: ['92%', '76%', '84%'] }} /></div>
  if (error) return <Alert type="error" showIcon message={t('localProductSalesAnalysis.errors.title')} description={localErrorDescription(error, t, i18n.resolvedLanguage)} action={<Button size="small" onClick={retry}>{t('common.retry')}</Button>} />
  // 传入 emptyText 时空态收成一行文字，不再用大图标占半屏。
  if (empty) return emptyText ? <div className={styles.emptyLine}>{emptyText}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('common.noData')} />
  return <>{children}</>
}

function DailyTrend({ data, label, mode, compact }: { data: LocalSupplierProductSalesAnalysisDaily[]; label: string; mode?: TrendChartMode; compact?: boolean }) {
  const { t } = useTranslation()
  if (!data.length) return <div className={styles.emptyLine}>{t('localProductSalesAnalysis.noTrendData')}</div>
  return <TrendChart data={data} ariaLabel={label} mode={mode} compact={compact} />
}

function formatPercent(value: number | null | undefined) { return value === null || value === undefined ? '—' : `${value.toFixed(1)}%` }

const CHIP_CLASS: Record<SellThroughLevel, string> = {
  none: styles.chipIdle,
  restock: styles.chipWarn,
  healthy: styles.chipGood,
  slow: styles.chipWarn,
  stale: styles.chipCritical,
}

/** 售进比状态标签：文案自带符号，不只靠颜色传达状态。 */
function SellThroughChip({ rate }: { rate: number | null | undefined }) {
  const { t } = useTranslation()
  const level = getSellThroughLevel(rate)
  return <span className={`${styles.chip} ${CHIP_CLASS[level]}`}>{t(`localProductSalesAnalysis.sellThrough.${level}`)}</span>
}

function Totals({ summary, caption, days }: { summary: LocalSupplierProductSalesAnalysisSummary | null; caption: ReactNode; days: number }) {
  const { t } = useTranslation()
  const totals = summary?.totals
  // 派生值全部由现有合计字段相除得到，不新增接口字段。
  const cells: [string, string, string, string?][] = [
    [t('localProductSalesAnalysis.metrics.purchaseQuantity'), formatQuantity(totals?.purchaseQuantity ?? 0), t('localProductSalesAnalysis.metrics.productCount', { count: summary?.total ?? 0 }), styles.dotPurchase],
    [t('localProductSalesAnalysis.metrics.purchaseAmount'), formatAud(totals?.purchaseAmount), t('localProductSalesAnalysis.metrics.averagePurchasePrice', { value: formatAud(safeDivide(totals?.purchaseAmount, totals?.purchaseQuantity)) })],
    [t('localProductSalesAnalysis.metrics.netSalesQuantity'), formatQuantity(totals?.netSalesQuantity ?? 0), t('localProductSalesAnalysis.metrics.dailyAverage', { value: (safeDivide(totals?.netSalesQuantity, days) ?? 0).toFixed(1) }), styles.dotSales],
    [t('localProductSalesAnalysis.metrics.netSalesAmount'), formatAud(totals?.netSalesAmount), t('localProductSalesAnalysis.metrics.averageSalesPrice', { value: formatAud(safeDivide(totals?.netSalesAmount, totals?.netSalesQuantity)) })],
  ]
  return <div className={styles.kpis}>
    <div className={styles.kpiCaption}>{caption}</div>
    {cells.map(([label, value, sub, dot]) => <div key={label} className={styles.kpi}><span className={styles.kpiLabel}>{dot ? <i className={`${styles.dot} ${dot}`} /> : null}{label}</span><strong>{value}</strong><small>{sub}</small></div>)}
    <div className={styles.kpi}>
      <span className={styles.kpiLabel}>{t('localProductSalesAnalysis.metrics.sellThroughRate')}<SellThroughChip rate={totals?.sellThroughRate} /></span>
      <strong>{formatPercent(totals?.sellThroughRate)}</strong>
      <div className={styles.meter}><i style={{ width: `${Math.min(100, Math.max(0, totals?.sellThroughRate ?? 0))}%` }} /></div>
    </div>
  </div>
}

export default function LocalProductSalesAnalysisPage() {
  const { t, i18n } = useTranslation()
  const defaultRange = useMemo(() => {
    const range = buildBrisbaneDefaultRange(30)
    return [dayjs(range.startDate), dayjs(range.endDate)] as [Dayjs, Dayjs]
  }, [])
  const [draftRange, setDraftRange] = useState<[Dayjs, Dayjs]>(defaultRange)
  const [draftKeyword, setDraftKeyword] = useState('')
  const [draftWarehouseCategoryGuids, setDraftWarehouseCategoryGuids] = useState<string[]>([])
  const [draftSupplierCodes, setDraftSupplierCodes] = useState<string[]>([])
  const [draftSupplierCategoryGuids, setDraftSupplierCategoryGuids] = useState<string[]>([])
  const [supplierCategoryOptions, setSupplierCategoryOptions] = useState<LocalSupplierProductSalesAnalysisSupplierCategoryOption[]>([])
  const [supplierCategoryOpen, setSupplierCategoryOpen] = useState(false)
  const [supplierCategorySearch, setSupplierCategorySearch] = useState('')
  const [supplierCategoryExpandedKeys, setSupplierCategoryExpandedKeys] = useState<string[]>([])
  const [supplierCategoryLoading, setSupplierCategoryLoading] = useState(false)
  const [supplierCategoryError, setSupplierCategoryError] = useState<string>()
  const [draftDocumentKeyword, setDraftDocumentKeyword] = useState('')
  const [quickDays, setQuickDays] = useState<number | null>(30)
  const [candidatePage, setCandidatePage] = useState(1)
  const [candidatePageSize, setCandidatePageSize] = useState(20)
  const [loadPhase, setLoadPhase] = useState<BootstrapMode | 'idle'>('bootstrap')
  const [bootstrapError, setBootstrapError] = useState<string>()
  const [candidatePaging, setCandidatePaging] = useState(false)
  const [sectionLoading, setSectionLoading] = useState<Partial<Record<LocalProductSalesAnalysisSectionKey, boolean>>>({})
  const [analysis, setAnalysis] = useState<LocalProductSalesAnalysisBootstrapState>(() => createEmptyLocalProductSalesAnalysisState())
  const [branchDaily, setBranchDaily] = useState<LocalSupplierProductSalesAnalysisDaily[]>([])
  const [selectedBranchCode, setSelectedBranchCode] = useState<string>()
  const [branchDailyLoading, setBranchDailyLoading] = useState(false)
  const [branchDailyError, setBranchDailyError] = useState<string>()
  const [chartMode, setChartMode] = useState<TrendChartMode>('daily')
  const analysisRef = useRef(analysis)
  const filterRef = useRef<LocalSupplierProductSalesAnalysisFilter>(requestFilter(defaultRange, ''))
  const selectionRef = useRef<LocalSupplierProductSalesAnalysisSelection>(createIncludedSelection())
  const currentProductRef = useRef<LocalSupplierProductSalesAnalysisCandidate | null>(null)
  const candidatePageRef = useRef(candidatePage)
  const candidatePageSizeRef = useRef(candidatePageSize)
  const migrateCurrentOnSummaryRef = useRef(false)
  const lastBootstrapRef = useRef<{ mode: BootstrapMode; context?: BootstrapContext }>({ mode: 'bootstrap' })
  const bootstrapGuardRef = useRef(createLatestRequestGuard())
  const paginationGuardRef = useRef(createLatestRequestGuard())
  const branchDailyGuardRef = useRef(createLatestRequestGuard())
  const bootstrapAbortRef = useRef<PageRequestTimeout>()
  const paginationAbortRef = useRef<PageRequestTimeout>()
  const branchDailyAbortRef = useRef<PageRequestTimeout>()
  const sectionAbortRefs = {
    options: useRef<PageRequestTimeout>(),
    summary: useRef<PageRequestTimeout>(),
    invoiceDetails: useRef<PageRequestTimeout>(),
    productDaily: useRef<PageRequestTimeout>(),
    branches: useRef<PageRequestTimeout>(),
  }
  const sectionGuards = {
    options: useRef(createLatestRequestGuard()).current,
    summary: useRef(createLatestRequestGuard()).current,
    invoiceDetails: useRef(createLatestRequestGuard()).current,
    productDaily: useRef(createLatestRequestGuard()).current,
    branches: useRef(createLatestRequestGuard()).current,
  }
  const brisbaneYesterday = dayjs(buildBrisbaneDefaultRange(1).endDate)

  useEffect(() => {
    if (!supplierCategoryOpen) return
    const controller = new AbortController()
    setSupplierCategoryLoading(true)
    setSupplierCategoryError(undefined)
    getLocalSupplierProductSalesAnalysisSupplierCategoryOptions(draftSupplierCodes, controller.signal)
      .then((response) => {
        if (controller.signal.aborted) return
        setSupplierCategoryOptions(response.data)
        const valid = new Set(collectSupplierCategoryLeafGuids(response.data))
        setDraftSupplierCategoryGuids((previous) => previous.filter((guid) => valid.has(guid)))
      })
      .catch((error) => {
        if (controller.signal.aborted) return
        setSupplierCategoryError(errorText(error, '供应商分类加载失败'))
      })
      .finally(() => { if (!controller.signal.aborted) setSupplierCategoryLoading(false) })
    return () => controller.abort()
  }, [supplierCategoryOpen, draftSupplierCodes])

  // 统一 bootstrap：挂载/查询/重置 autoSelectFirst；刷新携带原选择/当前商品且 forceRefresh；
  // 新请求先作废旧请求与旧超时，竞态旧响应由 guard 丢弃，成功后一次状态提交原子替换。
  const runBootstrap = useCallback((mode: BootstrapMode, context?: BootstrapContext) => {
    const migrateCurrentOnSummary = mode === 'switch' && migrateCurrentOnSummaryRef.current
    // 迁移意图属于本次请求；立即清空共享标志，避免后续 refresh 继承被 guard 丢弃的旧请求状态。
    migrateCurrentOnSummaryRef.current = false
    lastBootstrapRef.current = { mode, context }
    bootstrapAbortRef.current?.abort()
    paginationAbortRef.current?.abort()
    paginationGuardRef.current.invalidate()
    setCandidatePaging(false)
    ;(['options', 'summary', 'invoiceDetails', 'productDaily', 'branches'] as const).forEach((key) => {
      sectionAbortRefs[key].current?.abort()
      sectionGuards[key].invalidate()
      setSectionLoading((prev) => ({ ...prev, [key]: false }))
    })
    branchDailyAbortRef.current?.abort()
    branchDailyGuardRef.current.invalidate()
    setBranchDailyLoading(false)
    const timeout = createPageRequestTimeout(PAGE_BOOTSTRAP_TIMEOUT_SECONDS)
    bootstrapAbortRef.current = timeout
    const token = bootstrapGuardRef.current.next()
    setBootstrapError(undefined)
    setLoadPhase(mode)
    if (mode !== 'refresh') {
      setSelectedBranchCode(undefined)
      setBranchDaily([])
      setBranchDailyError(undefined)
      setBranchDailyLoading(false)
      if (mode === 'bootstrap') setAnalysis(createEmptyLocalProductSalesAnalysisState())
      else setAnalysis(clearLocalProductSalesAnalysisDetailSections(analysisRef.current))
    }
    const autoSelectFirst = mode === 'bootstrap'
    const forceRefresh = mode === 'refresh'
    const selection = context?.selection ?? (mode === 'refresh' ? selectionRef.current : undefined)
    const requestedProductCode = context?.currentProductCode ?? (mode === 'refresh' ? currentProductRef.current?.productCode : undefined)
    const body = buildLocalProductSalesAnalysisBootstrapRequest({
      filter: filterRef.current,
      selection,
      currentProductCode: requestedProductCode,
      autoSelectFirst,
      forceRefresh,
      candidatePageNumber: candidatePageRef.current,
      candidatePageSize: candidatePageSizeRef.current,
      summaryPageNumber: 1,
      summaryPageSize: 50,
    })
    queryLocalSupplierProductSalesAnalysisBootstrap(body, timeout.signal).then((result) => {
      if (!bootstrapGuardRef.current.isCurrent(token)) return
      timeout.clear()
      const previousProductCode = currentProductRef.current?.productCode
      const next = applyLocalProductSalesAnalysisBootstrapResult(result.data, analysisRef.current)
      let currentProduct = next.currentProduct
      if (migrateCurrentOnSummary && !currentProduct) {
        currentProduct = getCurrentProductAfterCancellation(currentProduct, next.summary?.items ?? [], true)
      }
      selectionRef.current = next.effectiveSelection
      currentProductRef.current = currentProduct
      const committed = currentProduct === next.currentProduct ? next : { ...next, currentProduct }
      setAnalysis(committed)
      if (previousProductCode !== currentProduct?.productCode) {
        setSelectedBranchCode(undefined)
        setBranchDaily([])
        setBranchDailyError(undefined)
        setBranchDailyLoading(false)
      }
      setLoadPhase('idle')
    }).catch((error) => {
      if (!bootstrapGuardRef.current.isCurrent(token)) return
      timeout.clear()
      setLoadPhase('idle')
      setBootstrapError(aborted(error) ? 'localProductSalesAnalysis.errors.timeout' : errorText(error, 'localProductSalesAnalysis.errors.load'))
    })
  }, [])

  useEffect(() => {
    runBootstrap('bootstrap')
    return () => {
      bootstrapAbortRef.current?.abort()
      bootstrapGuardRef.current.invalidate()
      paginationAbortRef.current?.abort()
      branchDailyAbortRef.current?.abort()
      ;(['options', 'summary', 'invoiceDetails', 'productDaily', 'branches'] as const).forEach((key) => sectionAbortRefs[key].current?.abort())
    }
  }, [runBootstrap])
  useEffect(() => { analysisRef.current = analysis }, [analysis])

  const guardedRequest = <T,>(guard: ReturnType<typeof createLatestRequestGuard>, abortRef: { current?: PageRequestTimeout }, start: () => void, call: (signal: AbortSignal) => Promise<{ data: T }>, commit: (data: T) => void, fail: (message: string) => void, settle: () => void) => {
    abortRef.current?.abort()
    const timeout = createPageRequestTimeout(PAGE_SECTION_TIMEOUT_SECONDS)
    abortRef.current = timeout
    const token = guard.next()
    start()
    call(timeout.signal).then((result) => {
      if (!guard.isCurrent(token)) return
      timeout.clear()
      commit(result.data)
    }).catch((error) => {
      if (!guard.isCurrent(token)) return
      timeout.clear()
      fail(aborted(error) ? 'localProductSalesAnalysis.errors.timeout' : errorText(error, 'localProductSalesAnalysis.errors.load'))
    }).finally(() => {
      if (guard.isCurrent(token)) settle()
    })
  }

  const stopBootstrapForSectionInteraction = () => {
    bootstrapAbortRef.current?.abort()
    bootstrapGuardRef.current.invalidate()
    setLoadPhase('idle')
    setBootstrapError(undefined)
  }

  const cancelAllAnalysisRequests = () => {
    stopBootstrapForSectionInteraction()
    ;(['options', 'summary', 'invoiceDetails', 'productDaily', 'branches'] as const).forEach((key) => {
      sectionAbortRefs[key].current?.abort()
      sectionGuards[key].invalidate()
    })
    branchDailyAbortRef.current?.abort()
    branchDailyGuardRef.current.invalidate()
    setBranchDailyLoading(false)
    setSectionLoading({})
  }

  const requestAnalysisSection = (
    key: LocalProductSalesAnalysisSectionKey,
    body: LocalSupplierProductSalesAnalysisRequest,
    onSuccess?: (data: unknown) => void,
  ) => {
    guardedRequest(sectionGuards[key], sectionAbortRefs[key],
      () => {
        setSectionLoading((prev) => ({ ...prev, [key]: true }))
        setAnalysis((prev) => clearLocalProductSalesAnalysisSectionError(prev, key))
      },
      (signal) => queryAnalysisSection(key, body, signal),
      (data) => {
        if (onSuccess) onSuccess(data)
        else setAnalysis((prev) => applyLocalProductSalesAnalysisSectionResult(prev, key, data))
      },
      (message) => setAnalysis((prev) => setLocalProductSalesAnalysisSectionError(prev, key, message)),
      () => setSectionLoading((prev) => ({ ...prev, [key]: false })),
    )
  }

  const loadSummaryForSelection = (selection: LocalSupplierProductSalesAnalysisSelection) => {
    stopBootstrapForSectionInteraction()
    setAnalysis((prev) => ({
      ...clearLocalProductSalesAnalysisSectionError(prev, 'summary'),
      effectiveSelection: selection,
      summary: null,
    }))
    requestAnalysisSection('summary', {
      filter: filterRef.current,
      selection,
      pageNumber: 1,
      pageSize: 50,
    })
  }

  const loadCurrentProductSections = (
    product: LocalSupplierProductSalesAnalysisCandidate,
    selection: LocalSupplierProductSalesAnalysisSelection,
  ) => {
    stopBootstrapForSectionInteraction()
    currentProductRef.current = product
    setSelectedBranchCode(undefined)
    setBranchDaily([])
    setBranchDailyError(undefined)
    setBranchDailyLoading(false)
    setAnalysis((prev) => {
      let next = clearLocalProductSalesAnalysisSectionError(prev, 'invoiceDetails')
      next = clearLocalProductSalesAnalysisSectionError(next, 'productDaily')
      next = clearLocalProductSalesAnalysisSectionError(next, 'branches')
      return {
        ...next,
        effectiveSelection: selection,
        currentProduct: product,
        invoiceDetails: null,
        productDaily: [],
        branches: [],
      }
    })
    const body = {
      filter: filterRef.current,
      selection,
      currentProductCode: product.productCode,
    }
    requestAnalysisSection('invoiceDetails', { ...body, pageNumber: 1, pageSize: 50 })
    requestAnalysisSection('productDaily', body)
    requestAnalysisSection('branches', body)
  }

  // 新筛选必须立即清空分店日趋势遗留，避免旧商品/旧筛选的钻取数据残留。
  const clearAnalysisState = () => {
    migrateCurrentOnSummaryRef.current = false
    setSelectedBranchCode(undefined)
    setBranchDaily([])
    setBranchDailyError(undefined)
    setBranchDailyLoading(false)
  }

  const applyFilters = () => {
    const rangeError = getDateRangeError(draftRange[0].format('YYYY-MM-DD'), draftRange[1].format('YYYY-MM-DD'), brisbaneYesterday.format('YYYY-MM-DD'))
    if (rangeError) { setLoadPhase('idle'); setBootstrapError(rangeError); return }
    const nextFilter = requestFilter(draftRange, draftKeyword, draftWarehouseCategoryGuids, draftSupplierCodes, draftSupplierCategoryGuids, draftDocumentKeyword)
    filterRef.current = nextFilter
    selectionRef.current = createIncludedSelection()
    currentProductRef.current = null
    migrateCurrentOnSummaryRef.current = false
    candidatePageRef.current = 1
    candidatePageSizeRef.current = 20
    setCandidatePage(1)
    setCandidatePageSize(20)
    clearAnalysisState()
    runBootstrap('bootstrap')
  }

  const resetFilters = () => {
    const range = (() => { const result = buildBrisbaneDefaultRange(30); return [dayjs(result.startDate), dayjs(result.endDate)] as [Dayjs, Dayjs] })()
    setDraftRange(range); setDraftKeyword(''); setDraftWarehouseCategoryGuids([]); setDraftSupplierCodes([]); setDraftSupplierCategoryGuids([]); setDraftDocumentKeyword(''); setQuickDays(30)
    const nextFilter = requestFilter(range, '')
    filterRef.current = nextFilter
    selectionRef.current = createIncludedSelection()
    currentProductRef.current = null
    migrateCurrentOnSummaryRef.current = false
    candidatePageRef.current = 1
    candidatePageSizeRef.current = 20
    setCandidatePage(1)
    setCandidatePageSize(20)
    clearAnalysisState()
    runBootstrap('bootstrap')
  }

  const setRangeDays = (days: number) => {
    const result = buildBrisbaneDefaultRange(days)
    setDraftRange([dayjs(result.startDate), dayjs(result.endDate)])
    setQuickDays(days)
  }

  const refresh = () => {
    runBootstrap('refresh')
  }
  const retryBootstrap = () => {
    const last = lastBootstrapRef.current
    runBootstrap(last.mode, last.context)
  }

  const updateCandidate = (candidate: LocalSupplierProductSalesAnalysisCandidate, checked: boolean) => {
    const next = applyCandidateSelection(selectionRef.current, candidate.productCode, checked)
    selectionRef.current = next
    const currentWasCancelled = !checked && currentProductRef.current?.productCode === candidate.productCode
    if (!hasSelection(next)) {
      cancelAllAnalysisRequests()
      setAnalysis((prev) => ({ ...clearLocalProductSalesAnalysisDetailSections(prev), effectiveSelection: next, currentProduct: null }))
      currentProductRef.current = null
      return
    }

    if (currentWasCancelled) {
      const fallback = [
        ...(analysisRef.current.candidates?.items ?? []),
        ...(analysisRef.current.summary?.items ?? []),
      ].find((item) => item.productCode !== candidate.productCode && isSelected(next, item.productCode))
      if (!fallback) {
        // 当前可见快照没有替代项时，交给 bootstrap 按稳定商品顺序解析跨页选择。
        migrateCurrentOnSummaryRef.current = true
        runBootstrap('switch', { selection: next, currentProductCode: candidate.productCode })
        return
      }
      migrateCurrentOnSummaryRef.current = false
      loadSummaryForSelection(next)
      loadCurrentProductSections(fallback, next)
      return
    }

    loadSummaryForSelection(next)
    if (!currentProductRef.current && checked) loadCurrentProductSections(candidate, next)
  }
  const selectAllFiltered = () => {
    const next = { mode: 'allFiltered' as const, includedProductCodes: [], excludedProductCodes: [] }
    selectionRef.current = next
    const current = currentProductRef.current
    if (current) {
      loadSummaryForSelection(next)
      return
    }
    const first = analysisRef.current.candidates?.items[0]
    if (first) {
      loadSummaryForSelection(next)
      loadCurrentProductSections(first, next)
      return
    }
    runBootstrap('switch', { selection: next })
  }
  const clearSelection = () => {
    const empty = createIncludedSelection()
    cancelAllAnalysisRequests()
    selectionRef.current = empty
    currentProductRef.current = null
    clearAnalysisState()
    setAnalysis((prev) => ({ ...clearLocalProductSalesAnalysisDetailSections(prev), effectiveSelection: empty, currentProduct: null }))
    setLoadPhase('idle')
  }

  // 候选分页只请求 candidates，不再触发 bootstrap 全量重查。
  const loadCandidatePage = (page: number, size: number) => {
    candidatePageRef.current = page
    candidatePageSizeRef.current = size
    setCandidatePage(page)
    setCandidatePageSize(size)
    bootstrapAbortRef.current?.abort()
    bootstrapGuardRef.current.invalidate()
    // 分页会取代仍在进行的 bootstrap/switch；同步收口加载态，避免按钮永久转圈。
    setLoadPhase('idle')
    guardedRequest(paginationGuardRef.current, paginationAbortRef,
      () => { setBootstrapError(undefined); setCandidatePaging(true) },
      (signal) => queryLocalSupplierProductSalesAnalysisCandidates({ filter: filterRef.current, selection: selectionRef.current, pageNumber: page, pageSize: size }, signal),
      (data) => { setBootstrapError(undefined); setAnalysis((prev) => ({ ...prev, candidates: data })) },
      (message) => setBootstrapError(message),
      () => setCandidatePaging(false),
    )
  }

  // 部分失败按卡片重试：保留分段 API，只替换目标分段。
  const retrySection = (key: LocalProductSalesAnalysisSectionKey) => {
    const body = {
      filter: filterRef.current,
      selection: selectionRef.current,
      currentProductCode: currentProductRef.current?.productCode,
    }
    requestAnalysisSection(
      key,
      key === 'summary' || key === 'invoiceDetails'
        ? { ...body, pageNumber: 1, pageSize: 50 }
        : body,
    )
  }

  // 分店钻取仍走 branch-daily 单端点。
  const loadBranchDaily = (branchCode?: string) => {
    const product = currentProductRef.current
    if (!product || !branchCode) { setBranchDaily([]); return }
    guardedRequest(branchDailyGuardRef.current, branchDailyAbortRef,
      () => { setBranchDailyError(undefined); setBranchDailyLoading(true) },
      (signal) => queryLocalSupplierProductSalesAnalysisBranchDaily({ filter: filterRef.current, selection: selectionRef.current, currentProductCode: product.productCode, branchCode }, signal),
      (data) => { setBranchDailyError(undefined); setBranchDaily(data) },
      (message) => setBranchDailyError(message),
      () => setBranchDailyLoading(false),
    )
  }

  // 分店排行默认选中第一名并加载其日趋势，右栏不再出现“点击分店”的空占位；
  // 切换商品/新查询会把 selectedBranchCode 清空，分店分段返回后在此重新选中。
  useEffect(() => {
    if (selectedBranchCode || !analysis.currentProduct || !analysis.branches.length) return
    const first = analysis.branches[0]
    setSelectedBranchCode(first.branchCode)
    loadBranchDaily(first.branchCode)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [analysis.branches, analysis.currentProduct, selectedBranchCode])

  const analysisLoading = loadPhase === 'bootstrap' || loadPhase === 'switch'
  const current = analysis.currentProduct
  const currentName = current?.productName || current?.itemNumber || current?.productCode
  // 汇总分段已带回每个已选商品的指标（每页 50 条），按商品编码索引后直接用于列表行与当前商品头。
  const summaryByCode = useMemo(() => new Map((analysis.summary?.items ?? []).map((item) => [item.productCode, item])), [analysis.summary])
  const currentSummary = current ? summaryByCode.get(current.productCode) : undefined
  const branchSalesTotal = useMemo(() => analysis.branches.reduce((sum, item) => sum + item.netSalesQuantity, 0), [analysis.branches])
  const branchSalesMax = useMemo(() => Math.max(1, ...analysis.branches.map((item) => item.netSalesQuantity)), [analysis.branches])
  const priceTiers = useMemo(() => buildBranchPriceTiers(analysis.branches), [analysis.branches])
  const activeDays = useMemo(() => [...analysis.productDaily].reverse().filter((item) => item.purchaseQuantity !== 0 || item.netSalesQuantity !== 0), [analysis.productDaily])
  const appliedFilter = filterRef.current
  const appliedDays = countInclusiveDays(appliedFilter.startDate, appliedFilter.endDate)
  const selectedBranch = analysis.branches.find((item) => item.branchCode === selectedBranchCode)
  const invoiceItems = analysis.invoiceDetails?.items ?? []
  const invoiceQuantity = invoiceItems.reduce((sum, item) => sum + item.quantity, 0)
  const invoiceAmount = invoiceItems.reduce((sum, item) => sum + item.amount, 0)
  const invoicePartial = (analysis.invoiceDetails?.total ?? 0) > invoiceItems.length

  const detailColumns: ColumnsType<LocalSupplierProductSalesAnalysisInvoiceDetail> = [
    { title: t('localProductSalesAnalysis.columns.invoiceNo'), dataIndex: 'invoiceNo', width: 118, render: (value) => value || '—' }, { title: t('localProductSalesAnalysis.columns.store'), width: 130, render: (_, row) => row.storeName || row.storeCode || '—' },
    { title: t('localProductSalesAnalysis.columns.supplier'), width: 140, render: (_, row) => row.supplierName || row.supplierCode || '—' }, { title: t('localProductSalesAnalysis.columns.date'), dataIndex: 'purchaseDate', width: 106, render: (value) => value || '—' },
    { title: t('localProductSalesAnalysis.columns.quantity'), dataIndex: 'quantity', align: 'right', width: 88, render: formatQuantity }, { title: t('localProductSalesAnalysis.columns.purchasePrice'), dataIndex: 'purchasePrice', align: 'right', width: 108, render: formatAud }, { title: t('localProductSalesAnalysis.columns.amount'), dataIndex: 'amount', align: 'right', width: 108, render: formatAud },
  ]
  const branchColumns: ColumnsType<LocalSupplierProductSalesAnalysisBranch> = [
    { title: '#', width: 36, render: (_, __, index) => <span className={styles.rank}>{index + 1}</span> },
    { title: t('localProductSalesAnalysis.columns.authorizedStore'), render: (_, row) => <><button type="button" className={styles.branchButton} onClick={() => { setSelectedBranchCode(row.branchCode); loadBranchDaily(row.branchCode) }}>{row.branchName || row.branchCode}</button><i className={styles.branchBar} style={{ width: `${Math.max(0, row.netSalesQuantity) / branchSalesMax * 100}%` }} /></> },
    { title: t('localProductSalesAnalysis.columns.netSalesQuantity'), dataIndex: 'netSalesQuantity', align: 'right', width: 72, render: formatQuantity },
    { title: t('localProductSalesAnalysis.columns.share'), align: 'right', width: 56, render: (_, row) => <span className={styles.rank}>{branchSalesTotal > 0 ? `${Math.round(row.netSalesQuantity / branchSalesTotal * 100)}%` : '—'}</span> },
    { title: t('localProductSalesAnalysis.columns.averageUnitPrice'), dataIndex: 'averageUnitPrice', align: 'right', width: 72, render: formatAud },
  ]
  const dailyColumns: ColumnsType<LocalSupplierProductSalesAnalysisDaily> = [
    { title: t('localProductSalesAnalysis.columns.date'), dataIndex: 'date' },
    { title: t('localProductSalesAnalysis.metrics.purchaseQuantity'), dataIndex: 'purchaseQuantity', align: 'right', render: (value: number) => value ? formatQuantity(value) : '—' },
    { title: t('localProductSalesAnalysis.columns.netSalesQuantity'), dataIndex: 'netSalesQuantity', align: 'right', render: formatQuantity },
    { title: t('localProductSalesAnalysis.columns.averageUnitPrice'), dataIndex: 'averageUnitPrice', align: 'right', render: formatAud },
  ]
  const supplierOptions = useMemo(() => {
    // 复制后按供应商名称排序，避免改动接口返回的共享选项；无名称时使用编码。
    return [...analysis.options.suppliers]
      .sort((a, b) => (a.name || a.code).localeCompare(b.name || b.code, 'en-AU', { sensitivity: 'base', numeric: true }))
      .map((item) => ({ value: item.code, label: item.name ? `${item.name} (${item.code})` : item.code }))
  }, [analysis.options.suppliers])
  const supplierNameByCode = useMemo(() => new Map(analysis.options.suppliers.map((item) => [item.code, item.name || item.code])), [analysis.options.suppliers])
  const supplierCategoryGroups = useMemo(() => {
    const groups = new Map<string, { label: string; options: { value: string; label: string }[] }>()
    type CategoryNodeOption = LocalSupplierProductSalesAnalysisSupplierCategoryOption & { children?: CategoryNodeOption[] }
    const leaves = (items: CategoryNodeOption[]): CategoryNodeOption[] => items.flatMap((item) => item.children?.length ? leaves(item.children) : [item])
    supplierCategoryOptions.forEach((rawItem) => {
      const item = rawItem as CategoryNodeOption
      leaves([item]).forEach((leaf) => {
        const existing = groups.get(item.supplierCode)
        const group = existing || { label: `${supplierNameByCode.get(item.supplierCode) || item.supplierCode} (${item.supplierCode})`, options: [] }
        group.options.push({ value: leaf.guid, label: leaf.name })
        groups.set(item.supplierCode, group)
      })
    })
    return [...groups.values()]
  }, [supplierCategoryOptions, supplierNameByCode])
  const categoryText = (zh: string, en: string) => i18n.resolvedLanguage?.startsWith('en') ? en : zh
  const supplierCategoryTree = useMemo(() => {
    const query = supplierCategorySearch.trim().toLocaleLowerCase()
    type CategoryNodeOption = LocalSupplierProductSalesAnalysisSupplierCategoryOption & { parentGuid?: string; children?: CategoryNodeOption[] }
    const grouped = new Map<string, CategoryNodeOption[]>()
    supplierCategoryOptions.forEach((item) => {
      const existing = grouped.get(item.supplierCode)
      if (existing) existing.push(item as CategoryNodeOption)
      else grouped.set(item.supplierCode, [item as CategoryNodeOption])
    })
    const nodes: DataNode[] = []
    const leafGuids = new Set<string>()
    grouped.forEach((items, supplierCode) => {
      const supplierName = supplierNameByCode.get(supplierCode) || supplierCode
      const buildNestedChildren = (nestedItems: CategoryNodeOption[]): DataNode[] => nestedItems.flatMap((item) => {
        const nested = item.children?.length ? buildNestedChildren(item.children) : undefined
        if (nested?.length) return [{ key: item.guid, title: item.name, children: nested }]
        leafGuids.add(item.guid)
        return [{ key: item.guid, title: item.name }]
      })
      // 顶层数组就是供应商分类 roots；孤儿根节点即使带有已停用父级的 parentGuid 也必须保留。
      const allChildren = buildNestedChildren(items)
      const filterChildren = (treeNodes: DataNode[]): DataNode[] => treeNodes.flatMap((node) => {
        const children = node.children ? filterChildren(node.children) : []
        const nodeMatches = !query || String(node.title).toLocaleLowerCase().includes(query)
        // 命中父分类时保留其完整后代，保证用户可以继续展开并勾选子分类。
        if (nodeMatches) return [node]
        if (children.length) return [{ ...node, children }]
        return []
      })
      const children = filterChildren(allChildren)
      if (!children.length) return
      const key = `supplier:${supplierCode}`
      nodes.push({ key, title: `${supplierName} (${supplierCode})`, children })
    })
    const expandedKeys: string[] = []
    const collectExpandedKeys = (treeNodes: DataNode[]) => treeNodes.forEach((node) => {
      if (!node.children?.length) return
      expandedKeys.push(String(node.key))
      collectExpandedKeys(node.children)
    })
    if (query) collectExpandedKeys(nodes)
    return { nodes, expandedKeys, leafGuids }
  }, [supplierCategoryOptions, supplierCategorySearch, supplierNameByCode])
  const visibleSupplierCategoryGuids = useMemo(() => {
    const leaves: string[] = []
    const visit = (nodes: DataNode[]) => nodes.forEach((node) => {
      if (node.children?.length) visit(node.children)
      else if (supplierCategoryTree.leafGuids.has(String(node.key))) leaves.push(String(node.key))
    })
    visit(supplierCategoryTree.nodes)
    return leaves
  }, [supplierCategoryTree.leafGuids, supplierCategoryTree.nodes])
  useEffect(() => {
    if (supplierCategorySearch.trim()) setSupplierCategoryExpandedKeys(supplierCategoryTree.expandedKeys)
  }, [supplierCategorySearch, supplierCategoryTree.expandedKeys])
  const limitSelection = (values: string[], limit: number, label: string) => {
    if (values.length > limit) message.warning(categoryText(`${label}最多选择 ${limit} 项`, `${label}: select up to ${limit}`))
    return values.slice(0, limit)
  }
  const selectVisibleSupplierCategories = () => {
    setDraftSupplierCategoryGuids(previous => limitSelection([...new Set([...previous, ...visibleSupplierCategoryGuids])], 100, categoryText('供应商分类', 'Supplier categories')))
  }
  const updateSupplierCategoryTreeSelection = (checkedKeys: Key[]) => {
    // 供应商根节点仅用于批量勾选；请求仍保持原有的分类 GUID 语义。
    const visibleLeafGuids = new Set(visibleSupplierCategoryGuids)
    const checkedVisibleLeafGuids = checkedKeys.filter((key) => visibleLeafGuids.has(String(key))).map(String)
    setDraftSupplierCategoryGuids((previous) => limitSelection(mergeVisibleSupplierCategorySelection(previous, checkedVisibleLeafGuids, visibleLeafGuids), 100, categoryText('供应商分类', 'Supplier categories')))
  }
  const closeSupplierCategoryDropdown = () => {
    setSupplierCategoryOpen(false)
    setSupplierCategorySearch('')
    setSupplierCategoryExpandedKeys([])
  }
  const selectionLabel = analysis.effectiveSelection.mode === 'included' ? t('localProductSalesAnalysis.selectedCount', { count: analysis.effectiveSelection.includedProductCodes.length }) : t('localProductSalesAnalysis.allFilteredSelected')

  return <PageContainer title={t('localProductSalesAnalysis.title')}>
    {/* 全页唯一一组查询入口：日期、快捷天数与四个筛选条件同处一条筛选栏 */}
    <Card className={styles.toolbar} bordered={false}>
      <div className={styles.filterBar}>
        <RangePicker value={draftRange} disabledDate={(date) => date.isAfter(brisbaneYesterday, 'day')} onChange={(value) => value?.[0] && value?.[1] && (setDraftRange([value[0], value[1]]), setQuickDays(null))} allowClear={false} />
        <Segmented value={quickDays ?? ''} options={[7, 30, 90].map((days) => ({ value: days, label: t('localProductSalesAnalysis.quickDays', { count: days }) }))} onChange={(value) => setRangeDays(Number(value))} />
        <Input className={styles.filterKeyword} value={draftKeyword} onChange={(event) => setDraftKeyword(event.target.value)} onPressEnter={applyFilters} placeholder={t('localProductSalesAnalysis.filters.keyword')} allowClear />
        <Select className={styles.filterSelect} mode="multiple" maxTagCount={0} maxTagPlaceholder={() => categoryText(`已选 ${draftSupplierCodes.length} 家`, `${draftSupplierCodes.length} selected`)} value={draftSupplierCodes} onChange={(codes) => { setDraftSupplierCodes(limitSelection(codes, 100, categoryText('供应商', 'Suppliers'))); setDraftSupplierCategoryGuids([]) }} placeholder={t('localProductSalesAnalysis.filters.supplier')} allowClear showSearch optionFilterProp="label" options={supplierOptions} notFoundContent={t('localProductSalesAnalysis.noSuppliers')} aria-label={t('localProductSalesAnalysis.filters.supplier')} />
        <Select className={styles.filterSelect} mode="multiple" maxTagCount={0} maxTagPlaceholder={() => categoryText(`已选 ${draftSupplierCategoryGuids.length} 项`, `${draftSupplierCategoryGuids.length} selected`)} value={draftSupplierCategoryGuids} onChange={values => setDraftSupplierCategoryGuids(limitSelection(values, 100, categoryText('供应商分类', 'Supplier categories')))} placeholder={categoryText('供应商分类（多选）', 'Supplier categories (multiple)')} allowClear showSearch optionFilterProp="label" options={supplierCategoryGroups.flatMap(group => group.options)} popupMatchSelectWidth={false} open={supplierCategoryOpen} onOpenChange={open => { if (open) setSupplierCategoryOpen(true); else closeSupplierCategoryDropdown() }} onSearch={setSupplierCategorySearch} loading={supplierCategoryLoading} notFoundContent={supplierCategoryError || (supplierCategoryLoading ? categoryText('加载中…', 'Loading…') : categoryText('暂无供应商分类', 'No supplier categories'))} aria-label={categoryText('供应商分类', 'Supplier categories')} popupRender={() => <><div className={styles.categoryHint}>{categoryText('按供应商展开分类；分类与仓库分类共同筛选商品。最多选 100 项。', 'Expand suppliers to choose categories; this filter combines with warehouse categories.')}</div><div className={styles.categoryActions}><Button size="small" type="link" onClick={selectVisibleSupplierCategories}>{categoryText('全选当前分类', 'Select visible categories')}</Button><span>{categoryText(`已选 ${draftSupplierCategoryGuids.length} 项`, `${draftSupplierCategoryGuids.length} selected`)}</span></div><div className={styles.categoryTree}>{supplierCategoryError ? <div className={styles.emptyLine}>{supplierCategoryError}</div> : supplierCategoryLoading ? <Skeleton active title={false} paragraph={{ rows: 3 }} /> : supplierCategoryTree.nodes.length ? <Tree checkable selectable={false} treeData={supplierCategoryTree.nodes} checkedKeys={draftSupplierCategoryGuids} expandedKeys={supplierCategoryExpandedKeys} onExpand={(keys) => setSupplierCategoryExpandedKeys(keys.map(String))} onCheck={(keys) => updateSupplierCategoryTreeSelection(Array.isArray(keys) ? keys : keys.checked)} /> : <div className={styles.emptyLine}>{categoryText('暂无供应商分类', 'No supplier categories')}</div>}</div><div className={styles.categoryFooter}><Button size="small" onClick={() => setDraftSupplierCategoryGuids([])}>{categoryText('清空', 'Clear')}</Button><Button size="small" type="primary" onClick={() => { closeSupplierCategoryDropdown(); applyFilters() }}>{categoryText('应用筛选', 'Apply filters')}</Button></div></>} />
        <Select className={styles.filterSelect} mode="multiple" maxTagCount={0} maxTagPlaceholder={() => categoryText(`已选 ${draftWarehouseCategoryGuids.length} 项`, `${draftWarehouseCategoryGuids.length} selected`)} value={draftWarehouseCategoryGuids} onChange={values => setDraftWarehouseCategoryGuids(limitSelection(values, 100, categoryText('仓库分类', 'Warehouse categories')))} placeholder={t('localProductSalesAnalysis.filters.category')} allowClear showSearch optionFilterProp="label" options={analysis.options.warehouseCategories.map((item) => ({ value: item.guid, label: item.name || item.guid }))} notFoundContent={t('localProductSalesAnalysis.noCategories')} aria-label={t('localProductSalesAnalysis.filters.category')} />
        <Input className={styles.filterSelect} value={draftDocumentKeyword} onChange={(event) => setDraftDocumentKeyword(event.target.value)} onPressEnter={applyFilters} placeholder={t('localProductSalesAnalysis.filters.invoiceNo')} allowClear />
        <Space>
          <Button icon={<SearchOutlined />} type="primary" loading={loadPhase === 'bootstrap'} onClick={applyFilters}>{t('common.query')}</Button>
          <Button icon={<ClearOutlined />} onClick={resetFilters}>{t('common.reset')}</Button>
          <Button icon={<ReloadOutlined />} loading={loadPhase === 'refresh'} onClick={refresh}>{t('common.refresh')}</Button>
        </Space>
      </div>
    </Card>
    {analysis.sectionErrors.options ? <Alert className={styles.optionsAlert} type="warning" showIcon message={t('localProductSalesAnalysis.errors.options')} description={localErrorDescription(analysis.sectionErrors.options, t, i18n.resolvedLanguage)} action={<Button size="small" onClick={() => retrySection('options')}>{t('common.retry')}</Button>} /> : null}
    {bootstrapError ? <Alert className={styles.optionsAlert} type="error" showIcon message={t('localProductSalesAnalysis.errors.title')} description={localErrorDescription(bootstrapError, t, i18n.resolvedLanguage)} action={<Button size="small" onClick={retryBootstrap}>{t('common.retry')}</Button>} /> : null}
    <Card className={styles.summaryCard} bordered={false}>
      <PanelState loading={analysisLoading || !!sectionLoading.summary} error={analysis.sectionErrors.summary} empty={!hasSelection(analysis.effectiveSelection)} emptyText={t('localProductSalesAnalysis.emptySelection')} retry={() => retrySection('summary')}>
        <Totals summary={analysis.summary} days={appliedDays} caption={<><strong>{t('localProductSalesAnalysis.totalsCaption')}</strong><span>{selectionLabel}</span><span>{appliedFilter.startDate} ~ {appliedFilter.endDate}</span><span>{t('localProductSalesAnalysis.salesNotRealtime')}</span></>} />
      </PanelState>
    </Card>
    <div className={styles.layout}>
      <Card className={styles.panel} title={t('localProductSalesAnalysis.productScope')} extra={<Space size={8}><span className={styles.muted}>{selectionLabel}</span><Button type="link" size="small" onClick={selectAllFiltered}>{t('localProductSalesAnalysis.selectAllFiltered')}</Button><Button type="link" size="small" onClick={clearSelection}>{t('common.clearSelection')}</Button></Space>} bordered={false}>
        <PanelState loading={loadPhase === 'bootstrap' || candidatePaging} error={undefined} empty={analysis.candidates !== null && !analysis.candidates.items.length} emptyText={t('localProductSalesAnalysis.emptyCandidates')} retry={retryBootstrap}>
          <div className={styles.candidates}>{analysis.candidates?.items.map((candidate) => {
            const metrics = isSelected(analysis.effectiveSelection, candidate.productCode) ? summaryByCode.get(candidate.productCode) : undefined
            return <div key={candidate.productCode} className={`${styles.candidate} ${current?.productCode === candidate.productCode ? styles.currentCandidate : ''}`}>
              <Checkbox checked={isSelected(analysis.effectiveSelection, candidate.productCode)} onClick={(event) => event.stopPropagation()} onChange={(event) => updateCandidate(candidate, event.target.checked)} />
              <button type="button" className={styles.candidateMain} disabled={!canSetCurrentProduct(analysis.effectiveSelection, candidate.productCode)} onClick={() => { if (canSetCurrentProduct(analysis.effectiveSelection, candidate.productCode)) loadCurrentProductSections(candidate, selectionRef.current) }}><ProductImage src={candidate.imageUrl} alt={candidate.productName || candidate.productCode} size={48} />
                <span className={styles.candidateText}><strong>{candidate.productName || candidate.itemNumber || candidate.productCode}</strong><span>{candidate.itemNumber || '—'} · {metrics ? t('localProductSalesAnalysis.candidateMetrics', { purchase: formatQuantity(metrics.purchaseQuantity), sales: formatQuantity(metrics.netSalesQuantity) }) : candidate.barcode || '—'}</span></span>
              </button>
              {metrics ? <span className={styles.candidateStat}><strong>{formatPercent(metrics.sellThroughRate)}</strong><SellThroughChip rate={metrics.sellThroughRate} /></span> : null}
            </div>
          })}</div>
          <Pagination className={styles.pagination} size="small" current={candidatePage} pageSize={candidatePageSize} total={analysis.candidates?.total ?? 0} showSizeChanger showTotal={(total) => t('localProductSalesAnalysis.candidateTotal', { count: total })} onChange={(page, size) => loadCandidatePage(page, size)} />
        </PanelState>
      </Card>
      <div className={styles.column}>
        <Card className={styles.panel} bordered={false}>
          <PanelState loading={analysisLoading} error={undefined} empty={!current} emptyText={t('localProductSalesAnalysis.emptyCurrent')} retry={() => retrySection('summary')}>
            <div className={styles.productHeader}>
              {current ? <ProductImage src={current.imageUrl} alt={currentName || t('localProductSalesAnalysis.product')} size={64} /> : null}
              <div className={styles.productTitle}>
                <h2>{currentName} {currentSummary ? <SellThroughChip rate={currentSummary.sellThroughRate} /> : null}</h2>
                <div className={styles.productMeta}>
                  <span>{t('localProductSalesAnalysis.meta.itemNumber')} <b>{current?.itemNumber || '—'}</b></span>
                  <span>{t('localProductSalesAnalysis.meta.barcode')} <b>{current?.barcode || '—'}</b></span>
                  <span>{current?.warehouseCategoryName || t('localProductSalesAnalysis.uncategorized')}</span>
                  {currentSummary?.suppliers.length ? <span>{currentSummary.suppliers.map((item) => item.name || item.code).join(' / ')}</span> : null}
                </div>
              </div>
              {currentSummary ? <div className={styles.productStats}>
                <div><span>{t('localProductSalesAnalysis.meta.purchaseAndSales')}</span><strong>{formatQuantity(currentSummary.purchaseQuantity)} / {formatQuantity(currentSummary.netSalesQuantity)}</strong></div>
                <div><span>{t('localProductSalesAnalysis.metrics.sellThroughRate')}</span><strong>{formatPercent(currentSummary.sellThroughRate)}</strong></div>
                <div><span>{t('localProductSalesAnalysis.meta.averagePurchasePrice')}</span><strong>{formatAud(safeDivide(currentSummary.purchaseAmount, currentSummary.purchaseQuantity))}</strong></div>
                <div><span>{t('localProductSalesAnalysis.meta.averageSalesPrice')}</span><strong>{formatAud(safeDivide(currentSummary.netSalesAmount, currentSummary.netSalesQuantity))}</strong></div>
              </div> : null}
            </div>
          </PanelState>
        </Card>
        <Card className={styles.panel} title={t('localProductSalesAnalysis.dailyTrend')} extra={<Space size={12} wrap><span className={styles.legend}><i className={`${styles.dot} ${styles.dotPurchase}`} />{t('localProductSalesAnalysis.metrics.purchaseQuantity')}</span><span className={styles.legend}><i className={`${styles.dot} ${styles.dotSales}`} />{t('localProductSalesAnalysis.metrics.netSalesQuantity')}</span><Segmented size="small" value={chartMode} onChange={(value) => setChartMode(value as TrendChartMode)} options={[{ value: 'daily', label: t('localProductSalesAnalysis.chart.modeDaily') }, { value: 'cumulative', label: t('localProductSalesAnalysis.chart.modeCumulative') }]} /></Space>} bordered={false}>
          <PanelState loading={analysisLoading || !!sectionLoading.productDaily} error={analysis.sectionErrors.productDaily} empty={!analysis.productDaily.length} emptyText={t('localProductSalesAnalysis.noTrendData')} retry={() => retrySection('productDaily')}>
            <DailyTrend data={analysis.productDaily} mode={chartMode} label={t('localProductSalesAnalysis.productTrendLabel', { product: currentName || t('localProductSalesAnalysis.currentProduct') })} />
            <details className={styles.dailyDetails}><summary>{t('localProductSalesAnalysis.chart.showTable')}</summary><MeasuredTable metricId="executive-sales-intelligence.local-product-sales-analysis.table-3" size="small" rowKey="date" columns={dailyColumns} dataSource={activeDays} pagination={false} scroll={{ y: 260 }} /></details>
          </PanelState>
        </Card>
        <Card className={styles.panel} title={t('localProductSalesAnalysis.invoiceDetails')} extra={invoiceItems.length ? <span className={styles.muted}>{t('localProductSalesAnalysis.invoiceSummary', { count: analysis.invoiceDetails?.total ?? invoiceItems.length })}</span> : null} bordered={false}>
          <PanelState loading={analysisLoading || !!sectionLoading.invoiceDetails} error={analysis.sectionErrors.invoiceDetails} empty={analysis.invoiceDetails !== null && !analysis.invoiceDetails.items.length} emptyText={t('localProductSalesAnalysis.emptyInvoices')} retry={() => retrySection('invoiceDetails')}>
            <MeasuredTable metricId="executive-sales-intelligence.local-product-sales-analysis.table-1" size="small" rowKey="detailGuid" columns={detailColumns} dataSource={analysis.invoiceDetails?.items} pagination={false} scroll={{ x: 'max-content' }}
              summary={() => invoiceItems.length ? <MeasuredTable.Summary.Row className={styles.tableTotal}><MeasuredTable.Summary.Cell index={0} colSpan={4}>{t(invoicePartial ? 'localProductSalesAnalysis.pageTotal' : 'localProductSalesAnalysis.total')}</MeasuredTable.Summary.Cell><MeasuredTable.Summary.Cell index={4} align="right">{formatQuantity(invoiceQuantity)}</MeasuredTable.Summary.Cell><MeasuredTable.Summary.Cell index={5} align="right">{formatAud(safeDivide(invoiceAmount, invoiceQuantity))}</MeasuredTable.Summary.Cell><MeasuredTable.Summary.Cell index={6} align="right">{formatAud(invoiceAmount)}</MeasuredTable.Summary.Cell></MeasuredTable.Summary.Row> : null} />
          </PanelState>
        </Card>
      </div>
      <div className={`${styles.column} ${styles.rightColumn}`}>
        <Card className={styles.panel} title={t('localProductSalesAnalysis.branchRanking')} extra={analysis.branches.length ? <span className={styles.muted}>{t('localProductSalesAnalysis.branchCount', { count: analysis.branches.length })}</span> : null} bordered={false}>
          <PanelState loading={analysisLoading || !!sectionLoading.branches} error={analysis.sectionErrors.branches} empty={!analysis.branches.length} emptyText={t('localProductSalesAnalysis.emptyBranches')} retry={() => retrySection('branches')}>
            {priceTiers.length ? <div className={styles.priceTiers}>
              {priceTiers.length > 1 ? <span className={`${styles.chip} ${styles.chipWarn}`}>{t('localProductSalesAnalysis.priceTiers.multiple', { count: priceTiers.length })}</span> : <span className={`${styles.chip} ${styles.chipGood}`}>{t('localProductSalesAnalysis.priceTiers.single')}</span>}
              {/* 价位过多时只给区间，避免把一长串均价塞进一行 */}
              <span>{priceTiers.length <= 3 ? priceTiers.map((tier) => t('localProductSalesAnalysis.priceTiers.item', { price: formatAud(tier.price), count: tier.branchCount })).join(' · ') : `${formatAud(priceTiers[0].price)} ~ ${formatAud(priceTiers[priceTiers.length - 1].price)}`}</span>
            </div> : null}
            <MeasuredTable metricId="executive-sales-intelligence.local-product-sales-analysis.table-2" size="small" rowKey="branchCode" columns={branchColumns} dataSource={analysis.branches} pagination={false} onRow={(record) => ({ className: selectedBranchCode === record.branchCode ? styles.currentBranch : '' })} />
          </PanelState>
        </Card>
        {selectedBranchCode ? <Card className={styles.panel} title={t('localProductSalesAnalysis.branchTrendTitle', { branch: selectedBranch?.branchName || selectedBranchCode })} extra={selectedBranch ? <span className={styles.muted}>{formatQuantity(selectedBranch.netSalesQuantity)} · {formatAud(selectedBranch.averageUnitPrice)}</span> : null} bordered={false}>
          <PanelState loading={branchDailyLoading} error={branchDailyError} empty={!branchDaily.length} emptyText={t('localProductSalesAnalysis.noTrendData')} retry={() => loadBranchDaily(selectedBranchCode)}><DailyTrend compact data={branchDaily} label={t('localProductSalesAnalysis.branchTrendLabel')} /></PanelState>
        </Card> : null}
      </div>
    </div>
  </PageContainer>
}
