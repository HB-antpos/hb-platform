import { LeftOutlined, ReloadOutlined, RightOutlined } from '@ant-design/icons'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useRef, useState, type FC } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getHolidayCategoryMap,
  getHolidaySalesReport,
  type HolidaySalesReport,
  type HolidaySupplierFilter,
} from '../../../services/holidaySalesReportService'
import { useAuthStore } from '../../../store/auth'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import { useReportQuery } from '../ReportWorkbench/useReportQuery'
import { defaultYear, formatMonthDay, resolveStore, seasonPhase, stepStore, storeOptions, yearOptions } from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import {
  BenchmarkCard,
  CategoryMapDrawer,
  CategoryTable,
  DailyChart,
  Footer,
  InsightCard,
  KpiRow,
  ProductBoards,
  StoreTable,
  type Tr,
} from './Sections'
import styles from './styles.module.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）。
registerPageMessages({ zh, en })

const REPORT_METRIC_ID = 'holiday-sales-report'
const MAP_METRIC_ID = 'holiday-sales-report-category-map'
const SUPPLIERS: { value: HolidaySupplierFilter; labelKey: string }[] = [
  { value: 'all', labelKey: 'supplier.all' },
  { value: '257', labelKey: 'supplier.kfc' },
  { value: '243', labelKey: 'supplier.brazco' },
]

type ViewMode = 'all' | 'store'

const HolidaySalesReportPage: FC = () => {
  const { t } = useTranslation()
  const tr = useCallback<Tr>((key, params) => t(`holidaySalesReport.${key}`, params) as string, [t])

  const { active } = useKeepAliveContext()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)

  // 悉尼时区的「今天」：决定默认年份与年份上限；页面被保活后重新激活时刷新，避免跨天后仍用旧日期。
  const today = useMemo(() => formatSydneyIsoDate(), [active])
  const [year, setYear] = useState(() => defaultYear(formatSydneyIsoDate()))
  const [supplier, setSupplier] = useState<HolidaySupplierFilter>('all')
  const [mode, setMode] = useState<ViewMode>('all')
  const [storeCode, setStoreCode] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  const [mapOpen, setMapOpen] = useState(false)
  const forceRefreshRef = useRef(false)

  // 分店授权范围由服务端按用户解析；这里只用来区分缓存键，并判断有没有可看的分店。
  const scopeKey = useMemo(() => {
    const codes = access.visibleStoreCodes()
    return codes == null ? 'ALL' : [...new Set(codes.map((code) => code.trim()).filter(Boolean))].sort().join('|')
  }, [access])
  const hasStoreScope = Boolean(currentUser) && scopeKey !== ''
  const baseKey = `holiday-sales:${currentUser?.userGUID ?? ''}:${scopeKey}:${year}:${supplier}`

  const loadReport = (branchCode?: string) => async (signal: AbortSignal) => {
    // 强制刷新只在点「刷新」时传给服务端，普通切换仍可命中服务端缓存。
    const forceRefresh = forceRefreshRef.current
    return { data: await getHolidaySalesReport({ year, supplierCode: supplier, branchCode, forceRefresh }, signal) }
  }

  // 合计视图始终加载：分店下拉与「分店排行」都要用它。
  const allQuery = useReportQuery<HolidaySalesReport>(`${baseKey}:ALL`, loadReport(), {
    active,
    enabled: hasStoreScope,
    refresh,
    metricId: REPORT_METRIC_ID,
  })
  const options = useMemo(() => storeOptions(allQuery.data), [allQuery.data])
  const activeStore = mode === 'store' ? resolveStore(storeCode, options) : null

  const storeQuery = useReportQuery<HolidaySalesReport>(`${baseKey}:${activeStore ?? ''}`, loadReport(activeStore ?? undefined), {
    active,
    enabled: hasStoreScope && mode === 'store' && activeStore !== null,
    refresh,
    metricId: REPORT_METRIC_ID,
  })

  const mapQuery = useReportQuery(
    `holiday-map:${currentUser?.userGUID ?? ''}:${scopeKey}:${year}`,
    async (signal) => ({ data: await getHolidayCategoryMap(year, signal) }),
    { active, enabled: hasStoreScope && mapOpen, metricId: MAP_METRIC_ID },
  )

  // 「刷新」要让本次触发的所有查询都带 forceRefresh（合计与单店查询在同一次提交里各自读取），
  // 它们的 effect 都排在本 effect 之前，所以这里再复位，之后切筛选仍走服务端缓存。
  useEffect(() => {
    forceRefreshRef.current = false
  }, [refresh])

  const single = mode === 'store'
  const query = single ? storeQuery : allQuery
  // 单店视图要等合计视图先出来（拿到分店列表）才知道默认看哪家店。
  const loading = single ? allQuery.loading || (activeStore !== null && storeQuery.loading) : allQuery.loading
  const report = query.data
  const error = single ? allQuery.error ?? storeQuery.error : allQuery.error
  const isEmpty = Boolean(report) && report!.totals.amount === 0 && report!.totals.purchaseQuantity === 0

  const onRefresh = () => {
    forceRefreshRef.current = true
    setRefresh((value) => value + 1)
  }
  const openStore = (code: string) => {
    setStoreCode(code)
    setMode('store')
  }

  const title = report
    ? single && report.branchName
      ? tr('titleStore', { year: report.year, store: `${report.branchCode} ${report.branchName}` })
      : tr('titleAll', { year: report.year })
    : tr('title')

  return (
    <div className={styles.page} aria-busy={loading}>
      <header className={styles.head}>
        <div className={styles.titleBlock}>
          <h1>
            {title}
            {report && (
              <span className={styles.phase}>
                {seasonPhase(report) === 'running'
                  ? tr('phase.running')
                  : tr('phase.ended', { date: formatMonthDay(report.holidayDate) })}
              </span>
            )}
          </h1>
          {report && (
            <p className={styles.subtitle}>
              {tr('subtitle', { from: formatMonthDay(report.salesFrom), through: formatMonthDay(report.salesThrough) })}
            </p>
          )}
        </div>
        <div className={styles.headActions}>
          <button type="button" className={styles.button} onClick={() => setMapOpen(true)}>
            {tr('actions.categoryMap')}
          </button>
          <button type="button" className={styles.button} onClick={onRefresh} disabled={loading} aria-label={tr('actions.refresh')} title={tr('actions.refresh')}>
            <ReloadOutlined spin={loading} />
          </button>
        </div>
      </header>

      <div className={styles.filters}>
        <span className={styles.filterLabel}>{tr('filters.view')}</span>
        <div className={styles.segment} role="group" aria-label={tr('filters.view')}>
          <button type="button" className={mode === 'all' ? styles.segmentOn : undefined} aria-pressed={mode === 'all'} onClick={() => setMode('all')}>
            {tr('view.all')}
          </button>
          <button type="button" className={mode === 'store' ? styles.segmentOn : undefined} aria-pressed={mode === 'store'} onClick={() => setMode('store')}>
            {tr('view.store')}
          </button>
        </div>
        {single && (
          <div className={styles.storePicker}>
            <button type="button" className={styles.stepButton} aria-label={tr('store.prev')} title={tr('store.prev')} disabled={options.length < 2} onClick={() => setStoreCode(stepStore(activeStore, options, -1))}>
              <LeftOutlined />
            </button>
            <select className={styles.select} aria-label={tr('filters.store')} value={activeStore ?? ''} onChange={(event) => setStoreCode(event.target.value)}>
              {activeStore === null && <option value="">{tr('store.placeholder')}</option>}
              {options.map((store) => (
                <option key={store.branchCode} value={store.branchCode}>
                  {store.branchCode} {store.branchName}
                </option>
              ))}
            </select>
            <button type="button" className={styles.stepButton} aria-label={tr('store.next')} title={tr('store.next')} disabled={options.length < 2} onClick={() => setStoreCode(stepStore(activeStore, options, 1))}>
              <RightOutlined />
            </button>
          </div>
        )}
        <span className={styles.filterLabel}>{tr('filters.year')}</span>
        <select className={styles.select} aria-label={tr('filters.year')} value={year} onChange={(event) => setYear(Number(event.target.value))}>
          {yearOptions(today).map((option) => (
            <option key={option} value={option}>{option}</option>
          ))}
        </select>
        <span className={styles.filterLabel}>{tr('filters.supplier')}</span>
        <div className={styles.chips} role="group" aria-label={tr('filters.supplier')}>
          {SUPPLIERS.map((option) => (
            <button key={option.value} type="button" className={supplier === option.value ? styles.chipOn : styles.chip} aria-pressed={supplier === option.value} onClick={() => setSupplier(option.value)}>
              {tr(option.labelKey)}
            </button>
          ))}
        </div>
      </div>

      {!hasStoreScope && <div className={styles.empty}>{tr('state.noAccess')}</div>}
      {hasStoreScope && loading && !report && (
        <div className={styles.empty}>
          {tr('state.loading')}
          {(single ? allQuery.slow || storeQuery.slow : allQuery.slow) && <div className={styles.hint}>{tr('state.slow')}</div>}
        </div>
      )}
      {hasStoreScope && !loading && !report && error && (
        <div className={styles.errorBox} role="alert">
          {tr('state.error', { message: error })}
          <button type="button" className={styles.button} onClick={onRefresh}>{tr('state.retry')}</button>
        </div>
      )}
      {hasStoreScope && !loading && !report && !error && <div className={styles.empty}>{tr('state.empty', { year })}</div>}
      {report && isEmpty && <div className={styles.empty}>{single ? tr('state.storeEmpty') : tr('state.empty', { year: report.year })}</div>}

      {report && !isEmpty && (
        <>
          <KpiRow report={report} single={single} tr={tr} allStoresSellThrough={report.benchmark?.sellThrough ?? null} />
          <div className={styles.row}>
            <DailyChart report={report} tr={tr} />
            <InsightCard report={report} tr={tr} />
          </div>
          <CategoryTable report={report} single={single} tr={tr} benchmark={report.benchmark} onOpenMap={() => setMapOpen(true)} />
          <ProductBoards report={report} single={single} tr={tr} />
          {single && report.benchmark ? <BenchmarkCard report={report} benchmark={report.benchmark} tr={tr} /> : <StoreTable report={report} tr={tr} onOpen={openStore} />}
          <Footer report={report} tr={tr} />
        </>
      )}

      <CategoryMapDrawer open={mapOpen} onClose={() => setMapOpen(false)} map={mapQuery.data} loading={mapQuery.loading} error={mapQuery.error} tr={tr} />
    </div>
  )
}

export default HolidaySalesReportPage
