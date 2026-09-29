import { DownloadOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Dropdown, Empty, Input, Pagination, Select, Skeleton, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PropsWithChildren } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { batchProductSalesApi } from '../../../services/batchProductSalesAnalysisService'
import { useAuthStore } from '../../../store/auth'
import { RequestError } from '../../../utils/request'
import type { BatchProductSalesApi, BatchSalesBranch, BatchSalesBranchOverview, BatchSalesCoverage, BatchSalesDaily, BatchSalesDetail, BatchSalesDiscountOverview, BatchSalesMetrics, BatchSalesQueryResult, BatchSalesScope } from '../../../types/batchProductSalesAnalysis'
import ProductImage from '../ProductFlowShared/ProductImage'
import { MAX_ITEM_NUMBERS, parsePastedItemNumbers, type ImportResult } from './import'
import { buildBatchProductSalesAnalysis, buildBatchProductSalesDetailExportScope, formatCsvRow, getBatchProductSalesClassifiedQuantity, getBatchProductSalesDateRangeError, getBatchProductSalesDiscountStateKey, hasBatchProductSalesDiscountStatisticsNotice, mergeBatchProductSalesDetailClassifications } from './logic'
import DiscountDailyChart from './DiscountDailyChart'
import type { DiscountChartDaily } from './chartModel'
import ProductScopeModal from './ProductScopeModal'
import styles from './index.module.css'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import messagesEn from './messages.en.json'
import messagesZh from './messages.zh.json'

// 本页文案随页面代码块懒注册：后台页面与订货前台「进货销量分析 · 粘贴数据查看」共用，避免整套文案进入首屏 i18n 包。
registerPageMessages({ zh: messagesZh, en: messagesEn })

const { RangePicker } = DatePicker
const ALL_PRODUCTS = '__batch-product-sales-all__'
const PRODUCT_PAGE_SIZE = 50
const AUTO_DISCOUNT_PRODUCT_LIMIT = 500
const quantityFormatter = new Intl.NumberFormat('en-AU')
const audFormatter = new Intl.NumberFormat('en-AU', {
  style: 'currency',
  currency: 'AUD',
})

interface BatchProductSalesAnalysisPageProps {
  api?: BatchProductSalesApi
  /** 嵌入订货前台或后台「进货销量分析」标签页时由外层页面提供标题，这里不再渲染自带的页头。 */
  embedded?: boolean
}

function PageFrame({ embedded, title, subtitle, children }: PropsWithChildren<{ embedded: boolean; title: string; subtitle: string }>) {
  return embedded ? <>{children}</> : <PageContainer title={title} subtitle={subtitle}>{children}</PageContainer>
}
interface BranchRankingRow extends BatchSalesBranch {
  rank: number
  contributingProductCount: number
  selectedProductCount: number
}

function businessToday() {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Australia/Brisbane',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date())
  const value = (type: string) => parts.find((part) => part.type === type)?.value ?? ''
  return `${value('year')}-${value('month')}-${value('day')}`
}
function defaultRange(today: string): [Dayjs, Dayjs] {
  const end = dayjs(today)
  return [end.subtract(29, 'day'), end]
}
function number(value: number | null | undefined) {
  return quantityFormatter.format(value ?? 0)
}
function money(value: number | null | undefined) {
  return value === null || value === undefined || !Number.isFinite(value) ? '—' : audFormatter.format(value)
}
function errorKind(error: unknown) {
  const text = error instanceof Error ? error.message : String(error ?? '')
  if (/403|forbidden|权限/i.test(text)) return 'forbidden'
  if (/abort|timeout|超时/i.test(text)) return 'timeout'
  return 'load'
}
function isAuthorizationError(error: unknown) {
  return error instanceof RequestError && (error.status === 401 || error.status === 403)
}
function isAbort(error: unknown) {
  return error instanceof Error && error.name === 'AbortError'
}
function isCoverageConflict(error: unknown) {
  if (error instanceof Error && (error.message === 'coverage' || error.message.includes('BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT'))) return true
  if (!(error instanceof RequestError) || error.status !== 409 || !error.payload || typeof error.payload !== 'object') return false
  const payload = error.payload as Record<string, unknown>
  return payload.errorCode === 'BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT' || payload.ErrorCode === 'BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT'
}
function classified(metrics: BatchSalesMetrics, field: 'regularQuantity' | 'discountQuantity' | 'unknownQuantity', unavailable = false) {
  const value = getBatchProductSalesClassifiedQuantity(metrics, field, unavailable)
  return value === null ? '—' : number(value)
}
function discountRate(metrics: BatchSalesMetrics) {
  return metrics.quantity <= 0 || metrics.discountStatus !== 'complete' || metrics.unknownQuantity !== 0 || metrics.regularQuantity < 0 || metrics.discountQuantity < 0 ? '—' : `${((metrics.discountQuantity / metrics.quantity) * 100).toFixed(1)}%`
}
function share(value: number, total: number) {
  return total === 0 ? null : value / total
}
function percent(value: number | null) {
  return value === null ? '—' : `${(value * 100).toFixed(1)}%`
}
function range(value1: number | null, value2: number | null) {
  return value1 === null || value2 === null ? '—' : value1 === value2 ? money(value1) : `${money(value1)} – ${money(value2)}`
}
function downloadCsv(fileName: string, rows: unknown[][]) {
  const content = `\uFEFF${rows.map(formatCsvRow).join('\r\n')}\r\n`
  const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}
function downloadCsvText(fileName: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  URL.revokeObjectURL(url)
}

function sameCoverage(left: BatchSalesCoverage, right: BatchSalesCoverage) {
  return left.version === right.version && sameStringSet(left.readyDates, right.readyDates)
}
function sameStringSet(left: string[], right: string[]) {
  if (left.length !== right.length) return false
  const sortedLeft = [...left].sort()
  const sortedRight = [...right].sort()
  return sortedLeft.every((value, index) => value === sortedRight[index])
}
function sameLockedScope(response: BatchSalesScope & { productCodes?: string[]; coverage: BatchSalesCoverage }, scope: BatchSalesScope, coverage: BatchSalesCoverage, productCodes?: string[]) {
  return sameCoverage(response.coverage, coverage)
    && response.startDate === scope.startDate && response.endDate === scope.endDate
    && sameStringSet(response.storeCodes, scope.storeCodes)
    && (!productCodes || (!!response.productCodes && sameStringSet(response.productCodes, productCodes)))
}
function requestCacheKey(sessionKey: string, scope: BatchSalesScope, coverage: BatchSalesCoverage, productCodes: string[], view: string, branchCode?: string) {
  return [sessionKey, scope.startDate, scope.endDate, [...scope.storeCodes].sort().join(','), coverage.version, [...coverage.readyDates].sort().join(','), [...productCodes].sort().join(','), view, branchCode ?? ''].join('|')
}
function mergeClassifications<T extends BatchSalesMetrics>(reliable: T, classifiedMetrics: BatchSalesMetrics): T {
  return { ...classifiedMetrics, quantity: reliable.quantity, salesAmount: reliable.salesAmount } as T
}

/** 首屏总览是可靠数量的唯一来源；用一个本地视图行复用既有图表与排行渲染，绝不触发商品详情请求。 */
function overviewDetail(result: BatchSalesQueryResult): BatchSalesDetail | undefined {
  if (!result.overview.metrics) return undefined
  return {
    startDate: result.startDate, endDate: result.endDate, storeCodes: result.storeCodes,
    productCodes: result.products.map((product) => product.productCode),
    product: { productCode: ALL_PRODUCTS, itemNumber: '', productName: '' },
    metrics: result.overview.metrics, daily: result.overview.daily, branches: result.overview.branches,
    warnings: result.warnings, coverage: result.coverage,
    discountStatisticStatus: result.discountStatisticStatus,
    discountUpdatedAt: result.discountUpdatedAt,
  }
}

/** 只在摘要承诺的 readyDates 补真实零；其他日期保留 null 断点。 */
function fillCoverageDays(data: BatchSalesDaily[], scope: BatchSalesScope, coverage: BatchSalesCoverage): DiscountChartDaily[] {
  const byDate = new Map(data.map((item) => [item.date, item]))
  const readyDates = new Set(coverage.readyDates)
  const dates: DiscountChartDaily[] = []
  for (let date = dayjs(scope.startDate); !date.isAfter(scope.endDate, 'day'); date = date.add(1, 'day')) {
    const key = date.format('YYYY-MM-DD')
    dates.push(
      !readyDates.has(key) ? { date: key, metrics: null } : byDate.get(key) ?? {
        date: key,
        metrics: {
          quantity: 0,
          regularQuantity: 0,
          discountQuantity: 0,
          unknownQuantity: 0,
          returnQuantity: 0,
          salesAmount: 0,
          discountStatus: 'complete',
          originalPriceMin: null,
          originalPriceMax: null,
          discountPriceMin: null,
          discountPriceMax: null,
        },
      },
    )
  }
  return dates
}

function LoadState({ loading, error, empty, onRetry, children }: { loading: boolean; error?: string; empty?: boolean; onRetry?: () => void; children: React.ReactNode }) {
  const { t } = useTranslation()
  if (loading)
    return (
      <div className={styles.state}>
        <Skeleton active title={false} paragraph={{ rows: 4 }} />
      </div>
    )
  if (error)
    return (
      <Alert
        type="error"
        showIcon
        message={t('batchProductSalesAnalysis.errors.title')}
        description={t(`batchProductSalesAnalysis.errors.${error}`)}
        action={
          onRetry ? (
            <Button size="small" onClick={onRetry}>
              {t('batchProductSalesAnalysis.retry')}
            </Button>
          ) : undefined
        }
      />
    )
  if (empty) return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('batchProductSalesAnalysis.noDailyData')} />
  return <>{children}</>
}

export default function BatchProductSalesAnalysisPage({ api = batchProductSalesApi, embedded = false }: BatchProductSalesAnalysisPageProps) {
  const { t } = useTranslation()
  const currentUser = useAuthStore((state) => state.currentUser)
  // 同一 GUID 的角色、精确权限或可见门店变动也必须隔离旧查询缓存。
  const sessionKey = useMemo(() => currentUser ? [
    currentUser.userGUID,
    [...(currentUser.exactPermissions ?? [])].sort().join(','),
    [...currentUser.roleNames].sort().join(','),
    [...(currentUser.stores ?? [])].map((store) => store.storeCode).sort().join(','),
  ].join('|') : 'anonymous', [currentUser])
  const today = useMemo(businessToday, [])
  const [draftRange, setDraftRange] = useState<[Dayjs, Dayjs]>(() => defaultRange(today))
  const [draftStores, setDraftStores] = useState<string[]>([])
  const [pastedText, setPastedText] = useState('')
  const [importResult, setImportResult] = useState<ImportResult>(() => parsePastedItemNumbers(''))
  const [options, setOptions] = useState<{
    stores: { code: string; name: string }[]
    maxItemNumbers: number
    maxDays: number
  }>()
  const [optionsLoading, setOptionsLoading] = useState(true)
  const [optionsError, setOptionsError] = useState<string>()
  const [queryResult, setQueryResult] = useState<BatchSalesQueryResult>()
  const [appliedScope, setAppliedScope] = useState<BatchSalesScope>()
  const [submittedInput, setSubmittedInput] = useState<{
    scope: BatchSalesScope
    itemNumbers: string[]
  }>()
  const [queryLoading, setQueryLoading] = useState(false)
  const [queryError, setQueryError] = useState<string>()
  const [detailsByProduct, setDetailsByProduct] = useState<Record<string, BatchSalesDetail>>({})
  const [branchOverview, setBranchOverview] = useState<BatchSalesBranchOverview>()
  const [detailFailures, setDetailFailures] = useState<Record<string, string>>({})
  const [detailLoading, setDetailLoading] = useState(false)
  const [branchLoading, setBranchLoading] = useState(false)
  const [branchError, setBranchError] = useState<string>()
  const [globalDiscountError, setGlobalDiscountError] = useState(false)
  const [detailDiscountError, setDetailDiscountError] = useState<string>()
  const [selectedProductCode, setSelectedProductCode] = useState(ALL_PRODUCTS)
  const [selectedBranchCode, setSelectedBranchCode] = useState<string>()
  const [branchSelectionTouched, setBranchSelectionTouched] = useState(false)
  const [scopeOpen, setScopeOpen] = useState(false)
  const [productSearch, setProductSearch] = useState('')
  const [productPage, setProductPage] = useState(1)
  const [matchesPage, setMatchesPage] = useState(1)
  const [branchSearch, setBranchSearch] = useState('')
  const [branchTopN, setBranchTopN] = useState(20)
  const [onlyBranchesWithSales, setOnlyBranchesWithSales] = useState(false)
  const [restoreRevision, setRestoreRevision] = useState(0)
  const queryAbortRef = useRef<AbortController>()
  const detailAbortRef = useRef<AbortController>()
  const branchAbortRef = useRef<AbortController>()
  const refreshAbortRefs = useRef(new Map<string, AbortController>())
  const exportAbortRef = useRef<AbortController>()
  const detailCacheRef = useRef(new Map<string, BatchSalesDetail>())
  const detailDiscountCacheRef = useRef(new Map<string, BatchSalesDiscountOverview>())
  const branchCacheRef = useRef(new Map<string, BatchSalesBranchOverview>())
  const branchDiscountLoadedRef = useRef(new Set<string>())
  const detailInflightRef = useRef(new Map<string, AbortController>())
  const detailDiscountInflightRef = useRef(new Map<string, AbortController>())
  const branchInflightRef = useRef(new Map<string, AbortController>())
  const requestGenerationRef = useRef(0)
  const previousSessionKeyRef = useRef(sessionKey)
  const queryRequestRef = useRef(0)
  const optionsRequestRef = useRef(0)
  const optionsAbortRef = useRef<AbortController>()
  const pendingSelectionRestoreRef = useRef<{ productCode: string; branchCode?: string; branchTouched: boolean }>()
  const selectedBranchRequestKeyRef = useRef<string>()
  const selectedDetailRequestKeyRef = useRef<string>()
  const productListRef = useRef<HTMLDivElement>(null)

  const clearQueryForAuthorization = useCallback(() => {
    requestGenerationRef.current += 1
    queryAbortRef.current?.abort()
    detailAbortRef.current?.abort()
    branchAbortRef.current?.abort()
    exportAbortRef.current?.abort()
    refreshAbortRefs.current.forEach((controller) => controller.abort())
    refreshAbortRefs.current.clear()
    detailCacheRef.current.clear()
    detailDiscountCacheRef.current.clear()
    branchCacheRef.current.clear()
    branchDiscountLoadedRef.current.clear()
    detailInflightRef.current.forEach((controller) => controller.abort())
    detailDiscountInflightRef.current.forEach((controller) => controller.abort())
    branchInflightRef.current.forEach((controller) => controller.abort())
    detailInflightRef.current.clear()
    detailDiscountInflightRef.current.clear()
    branchInflightRef.current.clear()
    selectedDetailRequestKeyRef.current = undefined
    selectedBranchRequestKeyRef.current = undefined
    setQueryResult(undefined)
    setAppliedScope(undefined)
    setSubmittedInput(undefined)
    setDetailsByProduct({})
    setBranchOverview(undefined)
    setDetailFailures({})
    setDetailLoading(false)
    setBranchLoading(false)
    setBranchError(undefined)
    setGlobalDiscountError(false)
    setDetailDiscountError(undefined)
    setQueryLoading(false)
    setQueryError(undefined)
    setSelectedProductCode(ALL_PRODUCTS)
    setSelectedBranchCode(undefined)
    setBranchSelectionTouched(false)
    setOptions(undefined)
    setDraftStores([])
    setProductPage(1)
    setMatchesPage(1)
  }, [])

  const loadOptions = useCallback(() => {
    optionsAbortRef.current?.abort()
    const controller = new AbortController()
    optionsAbortRef.current = controller
    const request = ++optionsRequestRef.current
    setOptionsLoading(true)
    setOptionsError(undefined)
    api
      .getOptions(controller.signal)
      .then((result) => {
        if (request === optionsRequestRef.current) setOptions(result)
      })
      .catch((error) => {
        if (isAbort(error) || controller.signal.aborted || request !== optionsRequestRef.current) return
        if (isAuthorizationError(error)) {
          clearQueryForAuthorization()
          return
        }
        setOptionsError(errorKind(error))
      })
      .finally(() => {
        if (request === optionsRequestRef.current) setOptionsLoading(false)
      })
    return () => {
      controller.abort()
      if (request === optionsRequestRef.current) optionsRequestRef.current += 1
    }
  }, [api, clearQueryForAuthorization])
  useEffect(() => loadOptions(), [loadOptions])
  useEffect(
    () => () => {
      queryAbortRef.current?.abort()
      detailAbortRef.current?.abort()
      detailDiscountInflightRef.current.forEach((controller) => controller.abort())
      branchAbortRef.current?.abort()
      optionsAbortRef.current?.abort()
      refreshAbortRefs.current.forEach((controller) => controller.abort())
      exportAbortRef.current?.abort()
      detailInflightRef.current.forEach((controller) => controller.abort())
      branchInflightRef.current.forEach((controller) => controller.abort())
    },
    [],
  )

  useLayoutEffect(() => {
    if (previousSessionKeyRef.current === sessionKey) return
    previousSessionKeyRef.current = sessionKey
    clearQueryForAuthorization()
    void loadOptions()
  }, [clearQueryForAuthorization, loadOptions, sessionKey])

  const dirty = !!submittedInput && (submittedInput.scope.startDate !== draftRange[0].format('YYYY-MM-DD') || submittedInput.scope.endDate !== draftRange[1].format('YYYY-MM-DD') || submittedInput.scope.storeCodes.join('|') !== draftStores.join('|') || submittedInput.itemNumbers.join('\u0001') !== importResult.itemNumbers.join('\u0001'))
  const productFilter = selectedProductCode === ALL_PRODUCTS ? undefined : selectedProductCode
  // synthetic ALL 总览和单品详情不能一起参与聚合，否则从单品切回全部会重复计量。
  const selectedDetails = useMemo(() => {
    const detail = detailsByProduct[productFilter ?? ALL_PRODUCTS]
    return detail ? [detail] : []
  }, [detailsByProduct, productFilter])
  const rankAnalysis = useMemo(
    () =>
      buildBatchProductSalesAnalysis(selectedDetails, {
        productCode: productFilter,
      }),
    [productFilter, selectedDetails],
  )
  // 分店钻取是按需请求，首屏不自动选择首家分店。
  const activeBranchCode = branchSelectionTouched ? selectedBranchCode : undefined
  const branchOverviewMatchesSelection = !!branchOverview && branchOverview.branch.branchCode === activeBranchCode
  // ALL 的分店日序列与贡献只能由按需 branch overview 提供；不能拿 synthetic ALL 总览伪造。
  const hasSingleProductBranchFallback = !!productFilter && selectedDetails.some((detail) => detail.branches.some((branch) => branch.branchCode === activeBranchCode))
  const branchAnalysisAvailable = !activeBranchCode || branchOverviewMatchesSelection || hasSingleProductBranchFallback
  const analysis = useMemo(
    () => branchOverview && branchOverview.branch.branchCode === activeBranchCode
      ? { metrics: branchOverview.branch.metrics, daily: branchOverview.branch.daily, branches: [branchOverview.branch], productContributions: branchOverview.products.map((product) => ({ product, metrics: product.metrics })) }
      : buildBatchProductSalesAnalysis(selectedDetails, {
        productCode: productFilter,
        branchCode: activeBranchCode,
      }),
    [activeBranchCode, branchOverview, productFilter, selectedDetails],
  )
  const selectedProduct = queryResult?.products.find((product) => product.productCode === productFilter)
  const filteredProducts = useMemo(() => {
    const search = productSearch.trim().toLocaleLowerCase()
    return (queryResult?.products ?? []).filter((product) => `${product.itemNumber} ${product.productName} ${product.englishName ?? ''}`.toLocaleLowerCase().includes(search))
  }, [productSearch, queryResult?.products])
  const selectedBranch = rankAnalysis.branches.find((branch) => branch.branchCode === activeBranchCode)
  const discountNotice = useMemo(() => {
    const priority = ['Failed', 'OutOfSync', 'Superseded', 'Unavailable', 'Partial', 'Refreshing', 'Backfilling', 'Running', 'Queued', 'Pending'] as const
    const counts = new Map<(typeof priority)[number], number>()
    selectedDetails.forEach((detail) => {
      if (!detail.discountStatisticStatus) return
      if (!hasBatchProductSalesDiscountStatisticsNotice(detail.discountStatisticStatus)) return
      const state = getBatchProductSalesDiscountStateKey(detail.discountStatisticStatus)
      if (state === 'Fresh') return
      counts.set(state, (counts.get(state) ?? 0) + 1)
    })
    const primary = priority.find((state) => counts.has(state))
    if (!primary) return undefined
    const severeStates: ReadonlyArray<(typeof priority)[number]> = ['Failed', 'OutOfSync', 'Superseded', 'Unavailable', 'Partial']
    return {
      type: severeStates.includes(primary) ? ('warning' as const) : ('info' as const),
      message: priority
        .filter((state) => counts.has(state))
        .map((state) => `${t(`batchProductSalesAnalysis.discountStates.${state}`)} × ${counts.get(state)}`)
        .join(' · '),
    }
  }, [selectedDetails, t])
  const classificationUnavailable = !!productFilter && selectedDetails.some((detail) => ['OutOfSync', 'Unavailable', 'Superseded'].includes(getBatchProductSalesDiscountStateKey(detail.discountStatisticStatus)))
  const totalDaily = appliedScope && queryResult ? fillCoverageDays(rankAnalysis.daily, appliedScope, queryResult.coverage) : []
  const branchDaily = appliedScope && activeBranchCode && queryResult && branchAnalysisAvailable ? fillCoverageDays(analysis.daily, appliedScope, queryResult.coverage) : []
  const appliedStoreText = useMemo(() => {
    if (!appliedScope?.storeCodes.length) return t('batchProductSalesAnalysis.allStores')
    const byCode = new Map((options?.stores ?? []).map((store) => [store.code, store]))
    if (appliedScope.storeCodes.length > 3)
      return t('batchProductSalesAnalysis.storeCount', {
        count: appliedScope.storeCodes.length,
      })
    return appliedScope.storeCodes
      .map((code) => {
        const store = byCode.get(code)
        return store ? `${store.name} (${code})` : code
      })
      .join(' · ')
  }, [appliedScope, options?.stores, t])
  // 大批量查询先展示可靠的净销量和销售额；折扣分类只在选中单个商品后读取。
  useEffect(() => {
    if (!queryResult || !appliedScope || !queryResult.products.length || !queryResult.coverage.readyDates.length) return
    setGlobalDiscountError(false)
    if (queryResult.products.length > AUTO_DISCOUNT_PRODUCT_LIMIT) return
    const controller = new AbortController()
    const generation = requestGenerationRef.current
    const productCodes = queryResult.products.map((product) => product.productCode)
    void api.getDiscounts({ ...appliedScope, productCodes, coverageVersion: queryResult.coverage.version, readyDates: queryResult.coverage.readyDates }, controller.signal)
      .then((discounts) => {
        const productCodes = queryResult.products.map((product) => product.productCode)
        if (generation !== requestGenerationRef.current || !sameLockedScope(discounts, appliedScope, queryResult.coverage, productCodes)) throw new Error('coverage')
        if (!queryResult.overview.metrics || !discounts.overview.metrics) return
        // 分类请求只能补分类字段；可靠 quantity 和 salesAmount 始终保留 query overview 的值。
        const patched = {
          ...queryResult,
          discountStatisticStatus: discounts.discountStatisticStatus,
          discountUpdatedAt: discounts.discountUpdatedAt,
          warnings: [...new Set([...queryResult.warnings, ...discounts.warnings])],
          overview: {
            ...queryResult.overview,
            metrics: mergeClassifications(queryResult.overview.metrics, discounts.overview.metrics),
            daily: queryResult.overview.daily.map((day) => {
              const next = discounts.overview.daily.find((item) => item.date === day.date)
              return next ? { ...next, metrics: mergeClassifications(day.metrics, next.metrics) } : day
            }),
            branches: queryResult.overview.branches.map((branch) => {
              const next = discounts.overview.branches.find((item) => item.branchCode === branch.branchCode)
              return next ? { ...next, metrics: mergeClassifications(branch.metrics, next.metrics), daily: branch.daily } : branch
            }),
          },
        }
        setQueryResult((current) => current && sameCoverage(current.coverage, queryResult.coverage) ? patched : current)
        const synthetic = overviewDetail(patched)
        if (synthetic) setDetailsByProduct((current) => current[ALL_PRODUCTS] && sameCoverage(current[ALL_PRODUCTS].coverage, queryResult.coverage) ? { ...current, [ALL_PRODUCTS]: synthetic } : current)
      })
      .catch((error) => {
        if (isAbort(error) || controller.signal.aborted || generation !== requestGenerationRef.current) return
        if (isAuthorizationError(error)) {
          clearQueryForAuthorization()
          return
        }
        if (isCoverageConflict(error)) setQueryError('statistics')
        else setGlobalDiscountError(true)
      })
    return () => controller.abort()
  }, [api, appliedScope, clearQueryForAuthorization, queryResult?.coverage.version, queryResult?.coverage.readyDates.join('|'), queryResult?.products, sessionKey])

  const query = useCallback((preserveCurrent = false) => {
    const input = preserveCurrent ? submittedInput : { scope: { startDate: draftRange[0].format('YYYY-MM-DD'), endDate: draftRange[1].format('YYYY-MM-DD'), storeCodes: [...draftStores] }, itemNumbers: importResult.itemNumbers }
    if (!input?.itemNumbers.length) {
      setQueryError('load')
      return
    }
    if (getBatchProductSalesDateRangeError(input.scope.startDate, input.scope.endDate, today)) {
      setQueryError('date')
      return
    }
    queryAbortRef.current?.abort()
    detailAbortRef.current?.abort()
    branchAbortRef.current?.abort()
    refreshAbortRefs.current.forEach((controller) => controller.abort())
    refreshAbortRefs.current.clear()
    exportAbortRef.current?.abort()
    detailCacheRef.current.clear()
    detailDiscountCacheRef.current.clear()
    branchCacheRef.current.clear()
    branchDiscountLoadedRef.current.clear()
    detailInflightRef.current.forEach((current) => current.abort())
    detailDiscountInflightRef.current.forEach((current) => current.abort())
    branchInflightRef.current.forEach((current) => current.abort())
    detailInflightRef.current.clear()
    detailDiscountInflightRef.current.clear()
    branchInflightRef.current.clear()
    selectedDetailRequestKeyRef.current = undefined
    selectedBranchRequestKeyRef.current = undefined
    const generation = ++requestGenerationRef.current
    const controller = new AbortController()
    queryAbortRef.current = controller
    const request = ++queryRequestRef.current
    const scope = input.scope
    const previousSelection = { productCode: selectedProductCode, branchCode: selectedBranchCode, branchTouched: branchSelectionTouched }
    setQueryLoading(true)
    setQueryError(undefined)
    if (!preserveCurrent) {
      setProductSearch('')
      setProductPage(1)
      setMatchesPage(1)
      setBranchSearch('')
      setBranchTopN(20)
      setOnlyBranchesWithSales(false)
    }
    api
      .query({ ...scope, itemNumbers: input.itemNumbers }, controller.signal)
      .then(async (result) => {
        if (request !== queryRequestRef.current || generation !== requestGenerationRef.current) return
        const resultScope = {
          startDate: result.startDate,
          endDate: result.endDate,
          storeCodes: result.storeCodes,
        }
        if (request !== queryRequestRef.current || generation !== requestGenerationRef.current) return
        // 首屏只替换 query/overview；商品和分店详情由用户选择后按需读取。
        setQueryResult(result)
        setProductPage(1)
        setMatchesPage(1)
        setAppliedScope(resultScope)
        const overview = overviewDetail(result)
        setDetailsByProduct(overview ? { [ALL_PRODUCTS]: overview } : {})
        setBranchOverview(undefined)
        setDetailFailures({})
        setDetailLoading(false)
        setBranchLoading(false)
        setBranchError(undefined)
        setGlobalDiscountError(false)
        setSubmittedInput({ scope, itemNumbers: [...input.itemNumbers] })
        if (!preserveCurrent) {
          setSelectedProductCode(ALL_PRODUCTS)
          setSelectedBranchCode(undefined)
          setBranchSelectionTouched(false)
        } else if (previousSelection.productCode === ALL_PRODUCTS || result.products.some((product) => product.productCode === previousSelection.productCode)) {
          // 仅成功刷新的新快照恢复一次选择；普通点击不进入此路径。
          const branchIsStillAvailable = !previousSelection.branchTouched || !previousSelection.branchCode || result.overview.branches.some((branch) => branch.branchCode === previousSelection.branchCode)
          pendingSelectionRestoreRef.current = branchIsStillAvailable
            ? previousSelection
            : { ...previousSelection, branchCode: undefined, branchTouched: false }
          if (!branchIsStillAvailable) {
            setSelectedBranchCode(undefined)
            setBranchSelectionTouched(false)
          }
          setRestoreRevision((current) => current + 1)
        } else {
          setSelectedProductCode(ALL_PRODUCTS)
          setSelectedBranchCode(undefined)
          setBranchSelectionTouched(false)
        }
      })
      .catch((error) => {
        if (!isAbort(error) && !controller.signal.aborted && request === queryRequestRef.current && generation === requestGenerationRef.current) {
          if (isAuthorizationError(error)) clearQueryForAuthorization()
          else setQueryError(isCoverageConflict(error) ? 'statistics' : errorKind(error))
        }
      })
      .finally(() => {
        if (request === queryRequestRef.current) setQueryLoading(false)
      })
  }, [api, branchSelectionTouched, clearQueryForAuthorization, draftRange, draftStores, importResult.itemNumbers, selectedBranchCode, selectedProductCode, submittedInput, today])
  const chooseProduct = (productCode: string, preserveBranchSelection = false) => {
    const currentCoverage = queryResult?.coverage
    const sameDetailKey = currentCoverage && appliedScope ? requestCacheKey(sessionKey, appliedScope, currentCoverage, [productCode], 'detail') : undefined
    const sameDiscountKey = currentCoverage && appliedScope ? requestCacheKey(sessionKey, appliedScope, currentCoverage, [productCode], 'detail-discounts') : undefined
    // 可靠详情和分类两条链都已有缓存或仍在读取时才复用。可靠详情失败而分类仍在途时必须允许重试。
    const hasReliableDetail = !!sameDetailKey && (detailCacheRef.current.has(sameDetailKey) || detailInflightRef.current.has(sameDetailKey))
    const hasClassifications = !!sameDiscountKey && (detailDiscountCacheRef.current.has(sameDiscountKey) || detailDiscountInflightRef.current.has(sameDiscountKey))
    if (!preserveBranchSelection && selectedProductCode === productCode && hasReliableDetail && hasClassifications) return
    // 先取消旧商品的两条链，再处理缓存快路，避免旧范围的详情或分类晚到写回。
    detailAbortRef.current?.abort()
    detailAbortRef.current = undefined
    detailInflightRef.current.forEach((controller) => controller.abort())
    detailDiscountInflightRef.current.forEach((controller) => controller.abort())
    detailInflightRef.current.clear()
    detailDiscountInflightRef.current.clear()
    setDetailLoading(false)
    setDetailDiscountError(undefined)
    setSelectedProductCode(productCode)
    if (!preserveBranchSelection) {
      setSelectedBranchCode(undefined)
      setBranchSelectionTouched(false)
    }
    setBranchOverview(undefined)
    setBranchLoading(false)
    setBranchError(undefined)
    selectedBranchRequestKeyRef.current = undefined
    selectedDetailRequestKeyRef.current = undefined
    branchInflightRef.current.forEach((controller) => controller.abort())
    branchInflightRef.current.clear()
    branchAbortRef.current?.abort()
    if (productCode === ALL_PRODUCTS || !queryResult || !appliedScope || !queryResult.coverage.readyDates.length) return
    const coverage = queryResult.coverage
    const detailKey = requestCacheKey(sessionKey, appliedScope, coverage, [productCode], 'detail')
    const discountKey = requestCacheKey(sessionKey, appliedScope, coverage, [productCode], 'detail-discounts')
    selectedDetailRequestKeyRef.current = detailKey
    const publish = (reliable: BatchSalesDetail) => {
      const discounts = detailDiscountCacheRef.current.get(discountKey)
      const next = discounts ? mergeBatchProductSalesDetailClassifications(reliable, discounts) : reliable
      setDetailsByProduct((current) => selectedDetailRequestKeyRef.current === detailKey ? { ...current, [productCode]: next } : current)
    }
    const cached = detailCacheRef.current.get(detailKey)
    if (cached) publish(cached)
    else if (!detailInflightRef.current.has(detailKey)) {
      const controller = new AbortController()
      detailAbortRef.current = controller
      detailInflightRef.current.set(detailKey, controller)
      const generation = requestGenerationRef.current
      setDetailLoading(true)
      void api.getDetail({ ...appliedScope, productCode, includeDiscounts: false, coverageVersion: coverage.version, readyDates: coverage.readyDates }, controller.signal)
        .then((detail) => {
          if (controller.signal.aborted || generation !== requestGenerationRef.current || selectedDetailRequestKeyRef.current !== detailKey) return
          if (!sameLockedScope(detail, appliedScope, coverage, [productCode])) {
            setQueryError('statistics')
            return
          }
          const normalized = { ...detail, branches: [...detail.branches].sort((left, right) => right.metrics.quantity - left.metrics.quantity) }
          detailCacheRef.current.set(detailKey, normalized)
          publish(normalized)
          setDetailFailures((current) => { const { [productCode]: _, ...rest } = current; return rest })
        })
        .catch((error) => {
          if (isAbort(error) || controller.signal.aborted || generation !== requestGenerationRef.current || selectedDetailRequestKeyRef.current !== detailKey) return
          if (isAuthorizationError(error)) {
            clearQueryForAuthorization()
            return
          }
          setDetailFailures((current) => ({ ...current, [productCode]: isCoverageConflict(error) ? 'statistics' : errorKind(error) }))
        })
        .finally(() => {
          if (detailInflightRef.current.get(detailKey) === controller) detailInflightRef.current.delete(detailKey)
          if (detailAbortRef.current === controller) setDetailLoading(false)
        })
    }
    // 与可靠详情并行读取单商品分类；不会把分类响应中的 quantity/salesAmount 当作可信来源。
    if (detailDiscountCacheRef.current.has(discountKey) || detailDiscountInflightRef.current.has(discountKey)) return
    const controller = new AbortController()
    detailDiscountInflightRef.current.set(discountKey, controller)
    const generation = requestGenerationRef.current
    void api.getDiscounts({ ...appliedScope, productCodes: [productCode], coverageVersion: coverage.version, readyDates: coverage.readyDates }, controller.signal)
      .then((discounts) => {
        if (controller.signal.aborted || generation !== requestGenerationRef.current || selectedDetailRequestKeyRef.current !== detailKey) return
        if (!sameLockedScope(discounts, appliedScope, coverage, [productCode])) {
          setQueryError('statistics')
          return
        }
        detailDiscountCacheRef.current.set(discountKey, discounts)
        setDetailDiscountError(undefined)
        const reliable = detailCacheRef.current.get(detailKey)
        if (reliable) publish(reliable)
      })
      .catch((error) => {
        if (isAbort(error) || controller.signal.aborted || generation !== requestGenerationRef.current || selectedDetailRequestKeyRef.current !== detailKey) return
        if (isAuthorizationError(error)) {
          clearQueryForAuthorization()
          return
        }
        // 分类失败不作为 detail failure：可靠销量继续显示，正价/折扣保留未知语义。
        if (isCoverageConflict(error)) {
          setQueryError('statistics')
          return
        }
        setDetailDiscountError(errorKind(error))
      })
      .finally(() => {
        if (detailDiscountInflightRef.current.get(discountKey) === controller) detailDiscountInflightRef.current.delete(discountKey)
      })
  }
  const chooseBranch = (branchCode: string) => {
    setSelectedBranchCode(branchCode)
    setBranchSelectionTouched(true)
    if (!queryResult || !appliedScope || !queryResult.coverage.readyDates.length) return
    const coverage = queryResult.coverage
    const productCodes = productFilter ? [productFilter] : queryResult.products.map((product) => product.productCode)
    const cacheKey = requestCacheKey(sessionKey, appliedScope, coverage, productCodes, 'branch', branchCode)
    selectedBranchRequestKeyRef.current = cacheKey
    if (branchInflightRef.current.has(cacheKey)) return
    // 无论接下来命中缓存还是创建请求，都先取消离开范围的 branch/discounts 链。
    branchAbortRef.current?.abort()
    branchInflightRef.current.forEach((controller, key) => {
      if (key !== cacheKey) controller.abort()
    })
    branchInflightRef.current.clear()
    setBranchLoading(false)
    setBranchError(undefined)
    const cached = branchCacheRef.current.get(cacheKey)
    if (branchOverview?.branch.branchCode === branchCode && sameStringSet(branchOverview.productCodes, productCodes) && sameCoverage(branchOverview.coverage, coverage) && branchDiscountLoadedRef.current.has(cacheKey)) return
    const controller = new AbortController()
    branchAbortRef.current = controller
    branchInflightRef.current.set(cacheKey, controller)
    const generation = requestGenerationRef.current
    if (!cached) setBranchLoading(true)
    const rawOverview = cached
      ? Promise.resolve(cached)
      : api.getBranchOverview({ ...appliedScope, productCodes, branchCode, coverageVersion: coverage.version, readyDates: coverage.readyDates }, controller.signal)
    void rawOverview
      .then((result) => {
        if (generation !== requestGenerationRef.current || selectedBranchRequestKeyRef.current !== cacheKey || !sameLockedScope(result, appliedScope, coverage, productCodes) || result.branch.branchCode !== branchCode || controller.signal.aborted) throw new Error('coverage')
        branchCacheRef.current.set(cacheKey, result)
        setBranchOverview(result)
        // 原始分店总览已是可靠销量；折扣补齐继续后台完成，不能遮住它。
        setBranchLoading(false)
        if (branchDiscountLoadedRef.current.has(cacheKey)) return undefined
        // 分店数量先由 branch 总览展示；分类由同一锁定范围的单次补齐请求覆盖，失败绝不把可靠量清零。
        if (productCodes.length > AUTO_DISCOUNT_PRODUCT_LIMIT) {
          branchDiscountLoadedRef.current.add(cacheKey)
          return undefined
        }
        return api.getDiscounts({ ...appliedScope, productCodes, branchCode, coverageVersion: coverage.version, readyDates: coverage.readyDates }, controller.signal)
          .then((discounts) => {
            if (generation !== requestGenerationRef.current || selectedBranchRequestKeyRef.current !== cacheKey || !sameLockedScope(discounts, appliedScope, coverage, productCodes) || discounts.branch?.branchCode !== branchCode || controller.signal.aborted) throw new Error('coverage')
            if (!discounts.branch) return
            const patched = {
              ...result,
              branch: {
                ...discounts.branch,
                metrics: mergeClassifications(result.branch.metrics, discounts.branch.metrics),
                daily: result.branch.daily.map((day) => {
                  const next = discounts.branch?.daily.find((item) => item.date === day.date)
                  return next ? { ...next, metrics: mergeClassifications(day.metrics, next.metrics) } : day
                }),
              },
              products: result.products.map((product) => {
                const next = discounts.products?.find((item) => item.productCode === product.productCode)
                return next ? { ...next, metrics: mergeClassifications(product.metrics, next.metrics) } : product
              }),
            }
            branchCacheRef.current.set(cacheKey, patched)
            branchDiscountLoadedRef.current.add(cacheKey)
            setBranchOverview((current) => selectedBranchRequestKeyRef.current === cacheKey && current?.branch.branchCode === branchCode && sameCoverage(current.coverage, coverage) ? patched : current)
          })
      })
      .catch((error) => {
        if (isAbort(error) || controller.signal.aborted || generation !== requestGenerationRef.current) return
        if (isAuthorizationError(error)) {
          clearQueryForAuthorization()
          return
        }
        setBranchError(isCoverageConflict(error) ? 'statistics' : errorKind(error))
      })
      .finally(() => {
        if (branchInflightRef.current.get(cacheKey) === controller) branchInflightRef.current.delete(cacheKey)
        if (branchAbortRef.current === controller) setBranchLoading(false)
      })
  }
  // 原子刷新替换快照后，仅消费一次恢复令牌；不能让普通点击或缓存写入重复发请求。
  useEffect(() => {
    const selection = pendingSelectionRestoreRef.current
    pendingSelectionRestoreRef.current = undefined
    if (!selection || !queryResult || (selection.productCode !== ALL_PRODUCTS && !queryResult.products.some((product) => product.productCode === selection.productCode))) return
    chooseProduct(selection.productCode, true)
    if (selection.branchTouched && selection.branchCode) chooseBranch(selection.branchCode)
  }, [restoreRevision])
  const clearFilters = () => {
    chooseProduct(ALL_PRODUCTS)
    setProductSearch('')
    setProductPage(1)
    setBranchSearch('')
    setBranchTopN(20)
    setOnlyBranchesWithSales(false)
  }
  const contributingProductCounts = useMemo(() => {
    if (!productFilter && queryResult) return new Map(queryResult.overview.branches.map((branch) => [branch.branchCode, branch.contributingProductCount]))
    const counts = new Map<string, number>()
    selectedDetails.forEach((detail) => {
      detail.branches.forEach((branch) => {
        if (branch.metrics.quantity !== 0 || branch.metrics.salesAmount !== 0) counts.set(branch.branchCode, (counts.get(branch.branchCode) ?? 0) + 1)
      })
    })
    return counts
  }, [productFilter, queryResult, selectedDetails])
  const selectedProductCount = productFilter ? 1 : (queryResult?.products.length ?? 0)
  const branchRows = useMemo<BranchRankingRow[]>(
    () =>
      rankAnalysis.branches
        .map((branch, index) => ({
          ...branch,
          rank: index + 1,
          contributingProductCount: contributingProductCounts.get(branch.branchCode) ?? 0,
          selectedProductCount,
        }))
        .filter((branch) => `${branch.branchName} ${branch.branchCode}`.toLocaleLowerCase().includes(branchSearch.trim().toLocaleLowerCase()))
        .filter((branch) => !onlyBranchesWithSales || branch.metrics.quantity !== 0)
        .slice(0, branchTopN),
    [branchSearch, branchTopN, contributingProductCounts, onlyBranchesWithSales, rankAnalysis, selectedProductCount],
  )
  const branchColumns: ColumnsType<BranchRankingRow> = [
    {
      title: t('batchProductSalesAnalysis.columns.rank'),
      dataIndex: 'rank',
      width: 58,
      align: 'right',
    },
    {
      title: t('batchProductSalesAnalysis.columns.branch'),
      width: 130,
      render: (_, row) => (
        <button
          className={styles.branchButton}
          aria-pressed={row.branchCode === activeBranchCode}
          aria-controls="batch-product-sales-branch-trend batch-product-sales-branch-contribution"
          onClick={() => chooseBranch(row.branchCode)}
        >
          {row.branchName || row.branchCode}
        </button>
      ),
    },
    {
      title: t('batchProductSalesAnalysis.columns.quantity'),
      width: 72,
      align: 'right',
      render: (_, row) => number(row.metrics.quantity),
    },
    {
      title: t('batchProductSalesAnalysis.columns.amount'),
      width: 106,
      align: 'right',
      render: (_, row) => money(row.metrics.salesAmount),
    },
    {
      title: t('batchProductSalesAnalysis.columns.regular'),
      width: 68,
      align: 'right',
      render: (_, row) => classified(row.metrics, 'regularQuantity', classificationUnavailable),
    },
    {
      title: t('batchProductSalesAnalysis.columns.discount'),
      width: 68,
      align: 'right',
      render: (_, row) => classified(row.metrics, 'discountQuantity', classificationUnavailable),
    },
    {
      title: t('batchProductSalesAnalysis.columns.rate'),
      width: 84,
      align: 'right',
      render: (_, row) => discountRate(row.metrics),
    },
    {
      title: t('batchProductSalesAnalysis.columns.contributingProducts'),
      width: 108,
      align: 'right',
      render: (_, row) => `${number(row.contributingProductCount)} / ${number(row.selectedProductCount)}`,
    },
  ]
  const contributionColumns: ColumnsType<(typeof analysis.productContributions)[number]> = [
    {
      title: t('batchProductSalesAnalysis.columns.itemNumber'),
      width: 104,
      render: (_, row) => row.product.itemNumber,
    },
    {
      title: t('batchProductSalesAnalysis.columns.product'),
      width: 156,
      render: (_, row) => row.product.productName || row.product.englishName || row.product.productCode,
    },
    {
      title: t('batchProductSalesAnalysis.columns.quantity'),
      width: 72,
      align: 'right',
      render: (_, row) => number(row.metrics.quantity),
    },
    {
      title: t('batchProductSalesAnalysis.columns.amount'),
      width: 104,
      align: 'right',
      render: (_, row) => money(row.metrics.salesAmount),
    },
    {
      title: t('batchProductSalesAnalysis.columns.regular'),
      width: 66,
      align: 'right',
      render: (_, row) => classified(row.metrics, 'regularQuantity', classificationUnavailable),
    },
    {
      title: t('batchProductSalesAnalysis.columns.discount'),
      width: 66,
      align: 'right',
      render: (_, row) => classified(row.metrics, 'discountQuantity', classificationUnavailable),
    },
    {
      title: t('batchProductSalesAnalysis.columns.productShare'),
      width: 84,
      align: 'right',
      render: (_, row) => percent(share(row.metrics.quantity, analysis.metrics.quantity)),
    },
  ]
  // 日期可用性只由结构化 coverage 决定；旧 statisticStatus 仍可说明后台状态，但不能挡住已发布日期的商品和折扣分类。
  const statisticsPending = queryResult?.coverage.status === 'pending'
  const noCompletedStatistics = t('batchProductSalesAnalysis.coveragePending')
  const detailExportReady = !!queryResult && queryResult.coverage.status !== 'pending' && !queryLoading
  const coverageReason = (reason: string) => t(`batchProductSalesAnalysis.coverageReasons.${reason}`, { defaultValue: reason })
  const coverageExportRows = queryResult ? [[t('batchProductSalesAnalysis.export.readyDates'), queryResult.coverage.readyDates.join(' | ')], [t('batchProductSalesAnalysis.export.pendingDates'), ...queryResult.coverage.pendingDates.map((item) => `${item.date}: ${coverageReason(item.reason)}`)], [t('batchProductSalesAnalysis.export.readyOnlyNotice')]] : []
  const exportSummary = () => queryResult && downloadCsv('batch-product-sales-summary.csv', [[t('batchProductSalesAnalysis.export.scope'), appliedScope?.startDate ?? '', appliedScope?.endDate ?? '', appliedScope?.storeCodes.join(' | ') || t('batchProductSalesAnalysis.allStores')], ...coverageExportRows, [t('batchProductSalesAnalysis.columns.itemNumber'), t('batchProductSalesAnalysis.columns.product'), t('batchProductSalesAnalysis.columns.quantity')], ...queryResult.products.map((product) => [product.itemNumber, product.productName || product.englishName || product.productCode, product.quantity ?? ''])])
  const exportDetail = () => {
    if (!appliedScope || !queryResult || !detailExportReady) return
    exportAbortRef.current?.abort()
    const controller = new AbortController()
    exportAbortRef.current = controller
    const generation = requestGenerationRef.current
    void api.exportDetail(buildBatchProductSalesDetailExportScope(appliedScope, queryResult.coverage, queryResult.products.map((product) => product.productCode), productFilter), controller.signal)
      .then((csv) => {
        if (controller.signal.aborted || generation !== requestGenerationRef.current) return
        downloadCsvText('batch-product-sales-detail.csv', csv)
      })
      .catch((error) => {
        if (isAbort(error) || controller.signal.aborted || generation !== requestGenerationRef.current) return
        if (isAuthorizationError(error)) {
          clearQueryForAuthorization()
          return
        }
        setQueryError(isCoverageConflict(error) ? 'statistics' : errorKind(error))
      })
  }
  const detailWarnings = [...new Set(selectedDetails.flatMap((detail) => detail.warnings))]
  const filtersAreDefault = selectedProductCode === ALL_PRODUCTS && !branchSelectionTouched && !productSearch && !branchSearch && branchTopN === 20 && !onlyBranchesWithSales

  return (
    <div className={styles.screen}>
      <PageFrame embedded={embedded} title={t('batchProductSalesAnalysis.title')} subtitle={t('batchProductSalesAnalysis.subtitle')}>
        <div className={styles.page}>
          <section className={styles.toolbar} aria-label={t('batchProductSalesAnalysis.query')}>
            <Button onClick={() => setScopeOpen(true)}>
              {t('batchProductSalesAnalysis.selectItems', {
                count: importResult.itemNumbers.length,
              })}
            </Button>
            <label className={styles.field}>
              <span>{t('batchProductSalesAnalysis.dateRange')}</span>
              <RangePicker value={draftRange} allowClear={false} disabledDate={(date) => date.isAfter(dayjs(today), 'day')} onChange={(value) => value?.[0] && value?.[1] && setDraftRange([value[0], value[1]])} />
            </label>
            <label className={styles.field}>
              <span>{t('batchProductSalesAnalysis.stores')}</span>
              <Select
                mode="multiple"
                value={draftStores}
                loading={optionsLoading}
                className={styles.storeSelect}
                maxTagCount="responsive"
                placeholder={t('batchProductSalesAnalysis.allStores')}
                options={options?.stores.map((store) => ({
                  value: store.code,
                  label: `${store.name} (${store.code})`,
                }))}
                onChange={setDraftStores}
              />
            </label>
            <Button type="primary" icon={<SearchOutlined />} loading={queryLoading} disabled={!importResult.itemNumbers.length} onClick={() => query()}>
              {queryLoading ? t('batchProductSalesAnalysis.querying') : t('batchProductSalesAnalysis.query')}
            </Button>
            {queryResult ? <Button icon={<ReloadOutlined />} loading={queryLoading} onClick={() => query(true)}>{t('batchProductSalesAnalysis.refreshResults')}</Button> : null}
            <Dropdown
              trigger={['click']}
              menu={{
                items: [
                  {
                    key: 'summary',
                    label: t('batchProductSalesAnalysis.downloadSummary'),
                    disabled: !queryResult || statisticsPending,
                    onClick: exportSummary,
                  },
                  {
                    key: 'detail',
                    label: t(queryResult && queryResult.products.length > AUTO_DISCOUNT_PRODUCT_LIMIT && !productFilter
                      ? 'batchProductSalesAnalysis.downloadNetDetail'
                      : 'batchProductSalesAnalysis.downloadDetail'),
                    disabled: !detailExportReady,
                    onClick: exportDetail,
                  },
                ],
              }}
            >
              <Button icon={<DownloadOutlined />}>{t('batchProductSalesAnalysis.exportResults')}</Button>
            </Dropdown>
          </section>
          {optionsError ? (
            <Alert
              type="warning"
              showIcon
              message={t('batchProductSalesAnalysis.errors.load')}
              action={
                <Button size="small" icon={<ReloadOutlined />} onClick={loadOptions}>
                  {t('batchProductSalesAnalysis.retry')}
                </Button>
              }
            />
          ) : null}
          {dirty ? <Alert type="info" showIcon message={t('batchProductSalesAnalysis.pendingQuery')} /> : null}
          {queryResult && queryError ? <Alert type="warning" showIcon message={t('batchProductSalesAnalysis.errors.title')} description={t(`batchProductSalesAnalysis.errors.${queryError}`)} action={<Button size="small" onClick={() => query(true)}>{t('batchProductSalesAnalysis.retry')}</Button>} /> : null}
          {queryResult && globalDiscountError ? <Alert type="warning" showIcon message={t('batchProductSalesAnalysis.discountLoadFailed')} action={<Button size="small" onClick={() => query(true)}>{t('batchProductSalesAnalysis.refreshStatus')}</Button>} /> : null}
          {queryResult && productFilter && detailDiscountError ? <Alert type="warning" showIcon message={t('batchProductSalesAnalysis.discountLoadFailed')} action={<Button size="small" onClick={() => chooseProduct(productFilter, true)}>{t('batchProductSalesAnalysis.retry')}</Button>} /> : null}
          {queryResult && queryResult.products.length > AUTO_DISCOUNT_PRODUCT_LIMIT ? <Alert type="info" showIcon message={t('batchProductSalesAnalysis.discountOnDemand', { count: AUTO_DISCOUNT_PRODUCT_LIMIT })} /> : null}
          {appliedScope ? (
            <div className={styles.appliedScope}>
              {t('batchProductSalesAnalysis.appliedScope', {
                startDate: appliedScope.startDate,
                endDate: appliedScope.endDate,
                stores: appliedStoreText,
              })}
            </div>
          ) : null}
          {queryResult && queryResult.coverage.status !== 'complete' ? (
            <Alert
              className={styles.warning}
              type={queryResult.coverage.status === 'pending' ? 'info' : 'warning'}
              showIcon
              message={queryResult.coverage.status === 'pending'
                ? t('batchProductSalesAnalysis.coveragePending')
                : t('batchProductSalesAnalysis.coverage', { ready: queryResult.coverage.readyDates.length, total: queryResult.coverage.readyDates.length + queryResult.coverage.pendingDates.length })}
              description={<>{t('batchProductSalesAnalysis.coverageScopeNotice')}{queryResult.coverage.pendingDates.length ? <details className={styles.coverageDetails}><summary>{t('batchProductSalesAnalysis.coverageDetails', { count: queryResult.coverage.pendingDates.length })}</summary>{queryResult.coverage.pendingDates.map((item) => <div key={item.date}><b>{item.date}</b><span>{coverageReason(item.reason)}</span></div>)}</details> : null}</>}
            />
          ) : null}
          {!statisticsPending && queryResult?.warnings.length ? (
            <details className={styles.hint}>
              <summary>{t('batchProductSalesAnalysis.dataNotes')}</summary>
              {queryResult.warnings.map((warning) => (
                <p key={warning}>{warning}</p>
              ))}
            </details>
          ) : null}
          {detailLoading ? <Alert type="info" showIcon message={t('batchProductSalesAnalysis.detailLoadingProgress', { completed: 0, total: 1 })} /> : null}
          {!detailLoading && Object.keys(detailFailures).length ? (
            <Alert
              type="warning"
              showIcon
              message={t('batchProductSalesAnalysis.partialDetailFailure', {
                count: Object.keys(detailFailures).length,
              })}
              action={<Button size="small" onClick={() => query(true)}>{t('batchProductSalesAnalysis.retry')}</Button>}
            />
          ) : null}
          {discountNotice ? (
            <Alert
              type={discountNotice.type}
              showIcon
              message={discountNotice.message}
              action={<Button size="small" onClick={() => query(true)}>{t('batchProductSalesAnalysis.refreshStatus')}</Button>}
            />
          ) : null}
          <main className={styles.layout}>
            <aside className={`${styles.column} ${styles.leftColumn}`}>
              <section className={`${styles.panel} ${styles.productPanel}`}>
                <header className={styles.panelHeader}>
                  <h2>
                    {t('batchProductSalesAnalysis.productList', {
                      count: queryResult?.products.length ?? 0,
                    })}
                  </h2>
                  {queryResult ? (
                    <span className={styles.panelMeta}>
                      {t('batchProductSalesAnalysis.readyDatesTotalQuantity', {
                        value: queryResult.coverage.status === 'pending' ? '—' : number(queryResult.products.reduce((sum, product) => sum + (product.quantity ?? 0), 0)),
                      })}
                    </span>
                  ) : null}
                </header>
                <Input allowClear prefix={<SearchOutlined />} aria-label={t('batchProductSalesAnalysis.searchProducts')} placeholder={t('batchProductSalesAnalysis.searchProducts')} value={productSearch} onChange={(event) => { setProductSearch(event.target.value); setProductPage(1); if (productListRef.current) productListRef.current.scrollTop = 0 }} />
                <LoadState loading={queryLoading && !queryResult} error={queryResult ? undefined : queryError} empty={!!queryResult && !queryResult.products.length} onRetry={() => query()}>
                    {queryResult ? (
                      <div
                        ref={productListRef}
                        className={styles.productList}
                        role="region"
                        aria-label={t('batchProductSalesAnalysis.productList', {
                          count: queryResult.products.length,
                        })}
                        tabIndex={0}
                      >
                        <button className={`${styles.productRow} ${selectedProductCode === ALL_PRODUCTS ? styles.productCurrent : ''}`} aria-pressed={selectedProductCode === ALL_PRODUCTS} onClick={() => chooseProduct(ALL_PRODUCTS)}>
                          <span className={styles.allProductsIcon}>Σ</span>
                          <span className={styles.productInfo}>
                            <strong>{t('batchProductSalesAnalysis.allProducts')}</strong>
                            <span>{t('batchProductSalesAnalysis.allProductsHint')}</span>
                          </span>
                          <b>{queryResult.coverage.status === 'pending' ? '—' : number(queryResult.products.reduce((sum, product) => sum + (product.quantity ?? 0), 0))}</b>
                        </button>
                        {filteredProducts
                          .slice((productPage - 1) * PRODUCT_PAGE_SIZE, productPage * PRODUCT_PAGE_SIZE)
                          .map((product) => (
                            <button key={product.productCode} className={`${styles.productRow} ${selectedProductCode === product.productCode ? styles.productCurrent : ''}`} aria-pressed={selectedProductCode === product.productCode} onClick={() => chooseProduct(product.productCode)}>
                              <ProductImage src={product.imageUrl} alt={product.productName || product.itemNumber} />
                              <span className={styles.productInfo}>
                                <strong>{product.itemNumber}</strong>
                                <span>{product.productName || product.englishName || product.productCode}</span>
                              </span>
                              <b>{product.quantity === null ? '—' : number(product.quantity)}</b>
                            </button>
                          ))}
                      </div>
                    ) : (
                      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('batchProductSalesAnalysis.noQueryResult')} />
                    )}
                </LoadState>
                {filteredProducts.length > PRODUCT_PAGE_SIZE ? <Pagination className={styles.productPagination} simple size="small" current={productPage} pageSize={PRODUCT_PAGE_SIZE} total={filteredProducts.length} onChange={(page) => { setProductPage(page); if (productListRef.current) productListRef.current.scrollTop = 0 }} showSizeChanger={false} /> : null}
                {queryResult?.matches.length ? (
                  <details className={styles.details}>
                    <summary>
                      {t('batchProductSalesAnalysis.matched')} {queryResult.matches.filter((match) => match.status === 'matched').length}
                      {' · '}
                      {t('batchProductSalesAnalysis.notFound')} {queryResult.matches.filter((match) => match.status === 'notFound').length}
                      {' · '}
                      {t('batchProductSalesAnalysis.ambiguous')} {queryResult.matches.filter((match) => match.status === 'ambiguous').length}
                    </summary>
                    {queryResult.matches.length > PRODUCT_PAGE_SIZE ? <Pagination className={styles.productPagination} simple size="small" current={matchesPage} pageSize={PRODUCT_PAGE_SIZE} total={queryResult.matches.length} onChange={setMatchesPage} showSizeChanger={false} /> : null}
                    {queryResult.matches.slice((matchesPage - 1) * PRODUCT_PAGE_SIZE, matchesPage * PRODUCT_PAGE_SIZE).map((match) => (
                      <div key={match.itemNumber}>
                        <span>{match.itemNumber}</span>
                        <span>{match.productCodes.join(', ') || '—'}</span>
                        <Tag color={match.status === 'matched' ? 'success' : match.status === 'ambiguous' ? 'warning' : 'error'}>{t(`batchProductSalesAnalysis.${match.status === 'notFound' ? 'notFound' : match.status}`)}</Tag>
                      </div>
                    ))}
                  </details>
                ) : null}
              </section>
            </aside>
            <section className={`${styles.panel} ${styles.totalTrendPanel}`}>
              <header className={styles.panelHeader}>
                <h2>{t('batchProductSalesAnalysis.dailyTrend')}</h2>
                <span className={styles.panelMeta}>{selectedProduct ? `${selectedProduct.itemNumber} · ${selectedProduct.productName || selectedProduct.englishName || selectedProduct.productCode}` : t('batchProductSalesAnalysis.allProducts')}</span>
              </header>
              <LoadState loading={detailLoading && !selectedDetails.length} empty={!detailLoading && !!queryResult && !totalDaily.length}>
                {selectedDetails.length ? (
                  <>
                    <DiscountDailyChart data={totalDaily} ariaLabel={t('batchProductSalesAnalysis.dailyTrend')} classificationUnavailable={classificationUnavailable} />
                    <div className={styles.branchTotals}>
                      <span>
                        {t('batchProductSalesAnalysis.metrics.quantity')} <b>{number(rankAnalysis.metrics.quantity)}</b>
                      </span>
                      <span className={styles.amount}>
                        {t('batchProductSalesAnalysis.metrics.amount')} <b>{money(rankAnalysis.metrics.salesAmount)}</b>
                      </span>
                      <span className={styles.regular}>
                        {t('batchProductSalesAnalysis.metrics.regular')} <b>{classified(rankAnalysis.metrics, 'regularQuantity', classificationUnavailable)}</b>
                      </span>
                      <span className={styles.discount}>
                        {t('batchProductSalesAnalysis.metrics.discount')} <b>{classified(rankAnalysis.metrics, 'discountQuantity', classificationUnavailable)}</b>
                      </span>
                      <span>
                        {t('batchProductSalesAnalysis.discountRate')} <b>{discountRate(rankAnalysis.metrics)}</b>
                      </span>
                    </div>
                    <p className={styles.priceHint}>
                      {t('batchProductSalesAnalysis.priceRange', {
                        original: range(rankAnalysis.metrics.originalPriceMin, rankAnalysis.metrics.originalPriceMax),
                        discount: range(rankAnalysis.metrics.discountPriceMin, rankAnalysis.metrics.discountPriceMax),
                      })}
                    </p>
                  </>
                ) : (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={statisticsPending ? noCompletedStatistics : t('batchProductSalesAnalysis.noQueryResult')} />
                )}
              </LoadState>
            </section>
            <section id="batch-product-sales-branch-trend" className={`${styles.panel} ${styles.branchTrendPanel}`}>
              <header className={styles.panelHeader}>
                <h2>{t('batchProductSalesAnalysis.branchDailyTrend')}</h2>
                {selectedBranch ? <span className={styles.panelMeta}>{selectedBranch.branchName || selectedBranch.branchCode}</span> : null}
              </header>
              {selectedBranch && activeBranchCode ? (
                <LoadState loading={branchLoading && !branchAnalysisAvailable} error={branchAnalysisAvailable ? undefined : branchError} empty={!branchAnalysisAvailable || !branchDaily.length}>
                  {branchAnalysisAvailable ? (
                    <>
                      {branchError ? <Alert className={styles.warning} type="warning" showIcon message={t(`batchProductSalesAnalysis.errors.${branchError}`)} /> : null}
                      <DiscountDailyChart
                        data={branchDaily}
                        ariaLabel={t('batchProductSalesAnalysis.branchTrend', {
                          branch: selectedBranch.branchName || selectedBranch.branchCode,
                        })}
                        classificationUnavailable={classificationUnavailable}
                      />
                      <div className={styles.branchTotals}>
                        <span>
                          {t('batchProductSalesAnalysis.metrics.quantity')} <b>{number(analysis.metrics.quantity)}</b>
                        </span>
                        <span className={styles.amount}>
                          {t('batchProductSalesAnalysis.metrics.amount')} <b>{money(analysis.metrics.salesAmount)}</b>
                        </span>
                        <span className={styles.regular}>
                          {t('batchProductSalesAnalysis.metrics.regular')} <b>{classified(analysis.metrics, 'regularQuantity', classificationUnavailable)}</b>
                        </span>
                        <span className={styles.discount}>
                          {t('batchProductSalesAnalysis.metrics.discount')} <b>{classified(analysis.metrics, 'discountQuantity', classificationUnavailable)}</b>
                        </span>
                      </div>
                    </>
                  ) : null}
                </LoadState>
              ) : (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={statisticsPending ? noCompletedStatistics : t('batchProductSalesAnalysis.selectBranch')} />
              )}
            </section>
            <section className={`${styles.panel} ${styles.dailyDetailPanel}`}>
              <header className={styles.panelHeader}>
                <h2>{t('batchProductSalesAnalysis.branchRanking')}</h2>
                <Button type="link" size="small" disabled={filtersAreDefault} onClick={clearFilters}>
                  {t('batchProductSalesAnalysis.clearFilters')}
                </Button>
              </header>
              {detailWarnings.map((warning) => (
                <Alert key={warning} className={styles.warning} type="warning" showIcon message={warning} />
              ))}
              <div className={styles.tableFilters}>
                <Input allowClear size="small" prefix={<SearchOutlined />} placeholder={t('batchProductSalesAnalysis.searchBranches')} value={branchSearch} onChange={(event) => setBranchSearch(event.target.value)} />
                <Select
                  size="small"
                  value={branchTopN}
                  options={[10, 20, 50, 100].map((value) => ({
                    value,
                    label: t('batchProductSalesAnalysis.topN', {
                      count: value,
                    }),
                  }))}
                  onChange={setBranchTopN}
                />
                <label>
                  <input type="checkbox" checked={onlyBranchesWithSales} onChange={(event) => setOnlyBranchesWithSales(event.target.checked)} /> {t('batchProductSalesAnalysis.onlyBranchesWithSales')}
                </label>
              </div>
              <div className={styles.tableWrap}>{branchRows.length ? <MeasuredTable metricId="executive-sales-intelligence.batch-product-sales-analysis.branch-ranking" size="small" rowKey="branchCode" columns={branchColumns} dataSource={branchRows} rowClassName={(row) => (row.branchCode === activeBranchCode ? styles.branchCurrent : '')} pagination={false} scroll={{ x: 674 }} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={statisticsPending ? noCompletedStatistics : t('batchProductSalesAnalysis.noBranchData')} />}</div>
            </section>
            <section id="batch-product-sales-branch-contribution" className={`${styles.panel} ${styles.branchSalesPanel}`}>
              <header className={styles.panelHeader}>
                <h2>{t('batchProductSalesAnalysis.branchProductContribution')}</h2>
                {selectedBranch ? <span className={styles.panelMeta}>{selectedBranch.branchName || selectedBranch.branchCode}</span> : null}
              </header>
              {activeBranchCode ? (
                <div className={styles.tableWrap}>
                  <LoadState loading={branchLoading && !branchAnalysisAvailable} error={branchAnalysisAvailable ? undefined : branchError} empty={!branchAnalysisAvailable || !analysis.productContributions.length}>
                    {branchAnalysisAvailable && analysis.productContributions.length ? <MeasuredTable metricId="executive-sales-intelligence.batch-product-sales-analysis.branch-product-contribution" size="small" rowKey={(row) => row.product.productCode} columns={contributionColumns} dataSource={analysis.productContributions} pagination={{ pageSize: PRODUCT_PAGE_SIZE, showSizeChanger: false, hideOnSinglePage: true }} scroll={{ x: 652 }} /> : null}
                  </LoadState>
                </div>
              ) : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={statisticsPending ? noCompletedStatistics : t('batchProductSalesAnalysis.selectBranch')} />}
            </section>
          </main>
          {scopeOpen ? (
            <ProductScopeModal
              initialText={pastedText}
              initialResult={importResult}
              maxItems={options?.maxItemNumbers ?? MAX_ITEM_NUMBERS}
              onCancel={() => setScopeOpen(false)}
              onApply={(text, result) => {
                setPastedText(text)
                setImportResult(result)
                setScopeOpen(false)
              }}
            />
          ) : null}
        </div>
      </PageFrame>
    </div>
  )
}
