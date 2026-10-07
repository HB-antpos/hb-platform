import {
  CloseCircleOutlined,
  DownloadOutlined,
  InfoCircleOutlined,
  LeftOutlined,
  LoadingOutlined,
  ReloadOutlined,
  RightOutlined,
  SearchOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { Alert, Button, message } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useRef, useState, type FC } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getMonthlyStoreDailySales, type MonthlyStoreDailySales } from '../../../services/monthlyDailySalesService'
import { useAuthStore } from '../../../store/auth'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import { useReportQuery } from '../ReportWorkbench/useReportQuery'
import type { ExportCheckpoint, ExportContext, ExportOutcome, ExportProgress, TextFn } from './export'
import {
  buildFileName,
  compareBranchCode,
  compositionOf,
  dayTextParams,
  defaultMonth,
  describeCoverage,
  displayTotals,
  EMPTY_CELL,
  formatAmount,
  formatDollars,
  formatPercent,
  isWeekend,
  listDates,
  matchesStoreSearch,
  monthTextParams,
  pickSelected,
  resolveFocus,
  selectionFingerprint,
  setCodesSelected,
  shiftMonth,
  summarizeSelection,
  summarizeStores,
  toggleExcluded,
  type DownloadFormat,
  type StoreSummary,
} from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import styles from './styles.module.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）。
registerPageMessages({ zh, en })

type ExporterModule = typeof import('./export')

/** 导出的界面状态：进行中显示进度条，中断后保留检查点供重试或只下载已完成的。 */
type ExportUiState =
  | { kind: 'idle' }
  | { kind: 'running'; progress: ExportProgress | null }
  | {
    kind: 'interrupted'
    fingerprint: string
    checkpoint: ExportCheckpoint
    stage: 'store' | 'finalize'
    failedName: string
    reason: string
    done: number
    total: number
    pending: number
  }

const MONTH_METRIC_ID = 'monthly-daily-sales-download'
const SKELETON_ROWS = 8

function classNames(...names: (string | false | null | undefined)[]) {
  return names.filter(Boolean).join(' ')
}

const MonthlyDailySalesDownloadPage: FC = () => {
  const { t } = useTranslation()
  // 页面文案都在 monthlyDailySalesDownload.* 下；tr 只是少写前缀，键名由契约测试静态核对。
  const tr = useCallback(
    (key: string, params?: Record<string, string | number>) => t(`monthlyDailySalesDownload.${key}`, params) as string,
    [t],
  )
  const text = useCallback<TextFn>((key, params) => t(key, params) as string, [t])

  const { active } = useKeepAliveContext()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)

  // 悉尼时区的「今天」：决定默认月份与月份上限；页面被保活后重新激活时刷新，避免跨天后仍用旧日期。
  const todayKey = useMemo(() => formatSydneyIsoDate(), [active])
  const [month, setMonth] = useState(() => defaultMonth(formatSydneyIsoDate()))
  const [search, setSearch] = useState('')
  // 勾选状态按「被排除的编码」保存：默认全选，换月后新出现的分店也默认勾选。
  const [excluded, setExcluded] = useState<ReadonlySet<string>>(() => new Set())
  const [focusCode, setFocusCode] = useState<string | null>(null)
  const [format, setFormat] = useState<DownloadFormat>('xlsx')
  const [refresh, setRefresh] = useState(0)
  const [exportState, setExportState] = useState<ExportUiState>({ kind: 'idle' })
  const forceRefreshRef = useRef(false)
  const exportAbortRef = useRef<AbortController | null>(null)

  // 授权分店范围：管理员 / 全局范围为 null，不传 branchCodes；有范围但为空则没有可看的分店。
  const branchCodes = useMemo(() => {
    const codes = access.visibleStoreCodes()
    return codes == null
      ? undefined
      : [...new Set(codes.map((code) => code.trim()).filter(Boolean))].sort(compareBranchCode)
  }, [access])
  const scopeKey = branchCodes ? branchCodes.join('|') : 'ALL'
  const hasStoreScope = Boolean(currentUser) && (branchCodes === undefined || branchCodes.length > 0)

  const reportQuery = useReportQuery<MonthlyStoreDailySales>(
    `monthly-daily-sales:${currentUser?.userGUID ?? ''}:${scopeKey}:${month}`,
    async (signal) => {
      // 强制刷新只在点「刷新」时传给服务端，普通切月仍可命中服务端缓存。
      const forceRefresh = forceRefreshRef.current
      forceRefreshRef.current = false
      return { data: await getMonthlyStoreDailySales({ month, branchCodes, forceRefresh }, signal) }
    },
    { active, enabled: hasStoreScope, refresh, metricId: MONTH_METRIC_ID },
  )
  const data = reportQuery.data
  const loading = reportQuery.loading
  const loadFailed = hasStoreScope && !loading && !data && Boolean(reportQuery.error)

  const summaries = useMemo(() => (data ? summarizeStores(data) : []), [data])
  const coverage = useMemo(() => (data ? describeCoverage(data) : null), [data])
  const visibleStores = useMemo(() => summaries.filter((store) => matchesStoreSearch(store, search)), [summaries, search])
  const selected = useMemo(() => pickSelected(summaries, excluded), [summaries, excluded])
  const selection = useMemo(() => summarizeSelection(selected), [selected])
  const focus = resolveFocus(summaries, focusCode)
  const maxFocusRevenue = useMemo(
    () => Math.max(1, ...(focus?.rows.map((row) => row.revenue ?? 0) ?? [])),
    [focus],
  )

  const exporting = exportState.kind === 'running'
  const monthParams = monthTextParams(month)
  const monthLabel = tr('monthLabel', { ...monthParams })
  const previousMonth = shiftMonth(month, -1, todayKey)
  const nextMonth = shiftMonth(month, 1, todayKey)
  const fingerprint = selectionFingerprint(month, format, selected.map((store) => store.branchCode))

  // 换月、改勾选或改格式后，旧的「导出中断」检查点就不再对应当前选择，直接收起。
  useEffect(() => {
    setExportState((current) => (current.kind === 'interrupted' && current.fingerprint !== fingerprint ? { kind: 'idle' } : current))
  }, [fingerprint])

  // 离开页面（组件卸载）时中止仍在进行的导出，不在后台继续处理。
  useEffect(() => () => exportAbortRef.current?.abort(), [])

  const reload = useCallback((force: boolean) => {
    forceRefreshRef.current = force
    setExportState((current) => (current.kind === 'interrupted' ? { kind: 'idle' } : current))
    setRefresh((value) => value + 1)
  }, [])

  const formatDay = useCallback((date: string) => tr('monthDay', { ...dayTextParams(date) }), [tr])
  const formatDayList = useCallback((dates: readonly string[]) => {
    const { shown, hidden } = listDates(dates)
    return `${shown.map(formatDay).join(tr('listSeparator'))}${hidden > 0 ? '…' : ''}`
  }, [formatDay, tr])

  // ---- 导出 ---------------------------------------------------------------

  const exportContext = useMemo<ExportContext | null>(
    () => (coverage ? { ...coverage, monthLabel } : null),
    [coverage, monthLabel],
  )

  /** 统一的导出启动器：同一时刻只允许一次，结果落到「完成 / 取消 / 中断」三种界面状态。 */
  const launch = useCallback(async (
    run: (exporter: ExporterModule, signal: AbortSignal, onProgress: (progress: ExportProgress) => void) => Promise<ExportOutcome>,
    outcomeFingerprint: string,
  ) => {
    if (exportAbortRef.current) return
    const controller = new AbortController()
    exportAbortRef.current = controller
    setExportState({ kind: 'running', progress: null })
    try {
      const exporter = await import('./export')
      const outcome = await run(exporter, controller.signal, (progress) => setExportState({ kind: 'running', progress }))
      if (outcome.status === 'completed') {
        message.success(tr('toast.exported', { fileName: outcome.fileName, count: outcome.storeCount }))
        setExportState({ kind: 'idle' })
      } else if (outcome.status === 'cancelled') {
        message.info(tr('toast.cancelled'))
        setExportState({ kind: 'idle' })
      } else {
        setExportState({
          kind: 'interrupted',
          fingerprint: outcomeFingerprint,
          checkpoint: outcome.checkpoint,
          stage: outcome.stage,
          failedName: outcome.failedStore?.branchName ?? '',
          reason: outcome.message,
          done: outcome.done,
          total: outcome.total,
          pending: outcome.pending,
        })
      }
    } catch {
      message.error(tr('toast.exportFailed'))
      setExportState({ kind: 'idle' })
    } finally {
      if (exportAbortRef.current === controller) exportAbortRef.current = null
    }
  }, [tr])

  const handleDownload = useCallback(async () => {
    if (exporting || !exportContext || selected.length === 0 || exportContext.throughDay === 0) return
    if (format === 'csv') {
      try {
        const exporter = await import('./export')
        const result = exporter.exportCsvFile(selected, exportContext, text)
        message.success(tr('toast.exportedCsv', { fileName: result.fileName, rows: result.rowCount }))
      } catch {
        message.error(tr('toast.exportFailed'))
      }
      return
    }
    void launch(
      (exporter, signal, onProgress) => exporter.startWorkbookExport({ stores: selected, context: exportContext, text, signal }, onProgress),
      fingerprint,
    )
  }, [exportContext, exporting, fingerprint, format, launch, selected, text, tr])

  const retryUnfinished = useCallback(() => {
    if (exportState.kind !== 'interrupted') return
    const { checkpoint, fingerprint: savedFingerprint } = exportState
    void launch((exporter, signal, onProgress) => exporter.resumeWorkbookExport(checkpoint, signal, onProgress), savedFingerprint)
  }, [exportState, launch])

  const downloadFinished = useCallback(() => {
    if (exportState.kind !== 'interrupted') return
    const { checkpoint, fingerprint: savedFingerprint } = exportState
    void launch(
      async (exporter) => ({ status: 'completed' as const, ...(await exporter.downloadCompletedStores(checkpoint)) }),
      savedFingerprint,
    )
  }, [exportState, launch])

  // ---- 派生展示 -----------------------------------------------------------

  const total = displayTotals(selection.totals)
  const kpi = (cents: number | null) => (loading ? <span className={styles.skeletonValue} aria-hidden="true" /> : formatDollars(cents))
  let freshness = ''
  if (coverage) {
    const params = {
      ...dayTextParams(coverage.throughDate ?? `${month}-01`),
      counted: coverage.countedDays,
      total: coverage.daysInMonth,
    }
    if (coverage.throughDay === 0) freshness = tr('freshness.none')
    else if (coverage.isPartial) freshness = tr('freshness.partial', params)
    else freshness = tr('freshness.complete', params)
  }

  const missingLead = selection.missingStores.length === 1
    ? tr('banner.missingLeadOne', {
      name: selection.missingStores[0].branchName,
      count: selection.missingStoreDays,
      dates: formatDayList(selection.missingStores[0].missingDates),
    })
    : tr('banner.missingLeadMany', {
      stores: selection.missingStores.length,
      count: selection.missingStoreDays,
      names: nameList(selection.missingStores, tr('listSeparator')),
    })
  const noSplitLead = selection.noSplitStores.length === 1
    ? tr('banner.noSplitLeadOne', {
      name: selection.noSplitStores[0].branchName,
      count: selection.noSplitStoreDays,
      dates: formatDayList(selection.noSplitStores[0].noSplitDates),
    })
    : tr('banner.noSplitLeadMany', {
      stores: selection.noSplitStores.length,
      count: selection.noSplitStoreDays,
      names: nameList(selection.noSplitStores, tr('listSeparator')),
    })

  const fileName = buildFileName(text('monthlyDailySalesDownload.export.fileNamePrefix'), month, format)
  const columnsLine = [
    tr('columns.date'), tr('columns.code'), tr('columns.name'), tr('columns.revenue'),
    tr('columns.card'), tr('columns.cash'), tr('columns.other'),
  ].join(tr('listSeparator'))
  const weekdayLabels = [
    tr('weekday.sun'), tr('weekday.mon'), tr('weekday.tue'), tr('weekday.wed'),
    tr('weekday.thu'), tr('weekday.fri'), tr('weekday.sat'),
  ]
  const focusTotals = focus ? displayTotals(focus.totals) : null
  const noData = Boolean(coverage && coverage.throughDay === 0)
  // 首次加载（还没有任何数据）时，标题 / 摘要显示「加载中」，不显示「暂无分店」之类的空状态文案。
  const showLoadingText = loading && !data
  let summaryLine: string
  if (showLoadingText) summaryLine = tr('state.loading')
  else if (selection.storeCount === 0) summaryLine = tr('footer.pickFirst')
  else if (format === 'xlsx') summaryLine = tr('footer.summaryXlsx', { sheets: selection.storeCount + 1, count: selection.storeCount })
  else summaryLine = tr('footer.summaryCsv', { rows: selection.csvRowCount, columns: columnsLine })
  const footerNotes: string[] = []
  if (coverage?.isPartial && coverage.throughDay > 0) {
    const params = { ...monthParams, last: coverage.throughDay, next: coverage.throughDay + 1 }
    footerNotes.push(coverage.throughDay === 1 ? tr('footer.partialOneDay', params) : tr('footer.partial', params))
  }
  if (noData) footerNotes.push(tr('footer.noData'))
  if (selection.missingStoreDays > 0) footerNotes.push(tr('footer.noteMissing', { count: selection.missingStoreDays }))
  if (selection.noSplitStoreDays > 0) footerNotes.push(tr('footer.noteNoSplit', { count: selection.noSplitStoreDays }))
  const canDownload = Boolean(data) && !loading && !exporting && selection.storeCount > 0 && !noData

  const progress = exportState.kind === 'running' ? exportState.progress : null
  const progressText = !progress
    ? tr('progress.preparing')
    : progress.stage === 'finalize'
      ? tr('progress.finalize')
      : tr('progress.store', { name: progress.storeName, index: progress.index, total: progress.total })

  const excludeUnreliable = () => {
    setExcluded((current) => setCodesSelected(current, selection.missingStores.map((store) => store.branchCode), false))
  }

  return (
    <div className={styles.page} aria-busy={loading}>
      <header className={styles.head}>
        <div className={styles.titleBlock}>
          <h1>{tr('title')}</h1>
          <span className={styles.subtitle}>{tr('subtitle')}</span>
        </div>
        <div className={styles.monthControls}>
          <span className={styles.controlLabel}>{tr('month.label')}</span>
          <div role="group" aria-label={tr('month.group')} className={styles.stepper}>
            <button
              type="button"
              className={styles.stepButton}
              aria-label={tr('month.previous')}
              disabled={!previousMonth || exporting}
              onClick={() => previousMonth && setMonth(previousMonth)}
            >
              <LeftOutlined />
            </button>
            <span className={styles.monthText} aria-live="polite">{monthLabel}</span>
            <button
              type="button"
              className={styles.stepButton}
              aria-label={tr('month.next')}
              disabled={!nextMonth || exporting}
              onClick={() => nextMonth && setMonth(nextMonth)}
            >
              <RightOutlined />
            </button>
          </div>
          <button
            type="button"
            className={styles.refreshButton}
            aria-label={tr('month.refresh')}
            title={tr('month.refresh')}
            disabled={loading || !hasStoreScope}
            onClick={() => reload(true)}
          >
            <ReloadOutlined />
          </button>
        </div>
      </header>

      <section className={styles.kpiBar} aria-label={tr('kpi.region')} aria-live="polite">
        <span className={styles.kpiLead}>{tr('kpi.selected', { count: selection.storeCount })}</span>
        <div className={styles.kpiItem}>
          <span>{tr('columns.revenue')}</span>
          <strong className={styles.kpiMain}>{kpi(total.revenue)}</strong>
        </div>
        <div className={styles.kpiItem}>
          <i className={classNames(styles.swatch, styles.swatchCard)} aria-hidden="true" />
          <span>{tr('columns.card')}</span>
          <strong>{kpi(total.card)}</strong>
          <em>{loading ? '' : formatPercent(selection.totals.card, selection.totals.splitRevenue)}</em>
        </div>
        <div className={styles.kpiItem}>
          <i className={classNames(styles.swatch, styles.swatchCash)} aria-hidden="true" />
          <span>{tr('columns.cash')}</span>
          <strong>{kpi(total.cash)}</strong>
          <em>{loading ? '' : formatPercent(selection.totals.cash, selection.totals.splitRevenue)}</em>
        </div>
        <div className={styles.kpiItem}>
          <i className={classNames(styles.swatch, styles.swatchOther)} aria-hidden="true" />
          <span>{tr('columns.other')}</span>
          <strong>{kpi(total.other)}</strong>
          <em>{loading ? '' : formatPercent(selection.totals.other, selection.totals.splitRevenue)}</em>
        </div>
        <div className={styles.kpiMeta}>
          {selection.missingStoreDays > 0 && <span className={styles.kpiWarn}>{tr('kpi.missing', { count: selection.missingStoreDays })}</span>}
          {selection.noSplitStoreDays > 0 && <span className={styles.kpiWarn}>{tr('kpi.noSplit', { count: selection.noSplitStoreDays })}</span>}
          {coverage?.isPartial && <span className={styles.tagPartial}>{tr('kpi.partial')}</span>}
          <span>{freshness}</span>
        </div>
      </section>

      {!hasStoreScope && <Alert type="info" showIcon message={tr('state.noScope')} />}
      {loadFailed && (
        <Alert
          type="error"
          showIcon
          message={tr('state.loadFailed')}
          action={<Button size="small" onClick={() => reload(false)}>{tr('state.retry')}</Button>}
        />
      )}

      <div className={styles.grid}>
        {/* ---------- 左：分店列表 ---------- */}
        <section className={classNames(styles.panel, styles.panelStores)} aria-label={tr('stores.region')} aria-busy={loading}>
          <div className={classNames(styles.progress, loading && styles.progressOn)} aria-hidden="true" />
          <div className={styles.panelHeader}>
            <div className={styles.panelTitle}>
              <i className={classNames(styles.dot, styles.dotBlue)} aria-hidden="true" />
              <h2>{tr('stores.title')}</h2>
              <span>{showLoadingText ? tr('state.loading') : tr('stores.hint', { count: visibleStores.length })}</span>
            </div>
            <div className={styles.panelActions}>
              <label className={styles.search}>
                <SearchOutlined aria-hidden="true" />
                <input
                  type="search"
                  aria-label={tr('stores.searchLabel')}
                  placeholder={tr('stores.searchPlaceholder')}
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                />
              </label>
              <button
                type="button"
                className={styles.smallButton}
                disabled={exporting || visibleStores.length === 0}
                onClick={() => setExcluded((current) => setCodesSelected(current, visibleStores.map((store) => store.branchCode), true))}
              >
                {tr('stores.selectAll')}
              </button>
              <button
                type="button"
                className={styles.smallButton}
                disabled={exporting || visibleStores.length === 0}
                onClick={() => setExcluded((current) => setCodesSelected(current, visibleStores.map((store) => store.branchCode), false))}
              >
                {tr('stores.clear')}
              </button>
            </div>
          </div>

          <div className={styles.scrollX}>
            <div role="table" aria-label={tr('stores.tableLabel')} className={styles.storeTable}>
              <div role="row" className={classNames(styles.storeRow, styles.headRow)}>
                <div role="columnheader" className={styles.cellCheck}>{tr('stores.colDownload')}</div>
                <div role="columnheader" className={styles.cellName}>{tr('stores.colStore')}</div>
                <div role="columnheader" className={styles.cellNum}>{tr('columns.revenue')}</div>
                <div role="columnheader" className={styles.cellNum}>{tr('columns.card')}</div>
                <div role="columnheader" className={styles.cellNum}>{tr('columns.cash')}</div>
                <div role="columnheader" className={styles.cellMix}>{tr('stores.colMix')}</div>
              </div>
              {loading && !data && Array.from({ length: SKELETON_ROWS }, (_, index) => (
                <div key={index} role="row" className={styles.skeletonRow} aria-hidden="true"><span /></div>
              ))}
              {visibleStores.map((store) => (
                <StoreListRow
                  key={store.branchCode}
                  store={store}
                  checked={!excluded.has(store.branchCode)}
                  focused={focus?.branchCode === store.branchCode}
                  disabled={exporting}
                  tr={tr}
                  formatDayList={formatDayList}
                  onToggle={() => setExcluded((current) => toggleExcluded(current, store.branchCode))}
                  onFocus={() => setFocusCode(store.branchCode)}
                />
              ))}
              {!loading && data && summaries.length === 0 && <div className={styles.empty}>{tr('stores.emptyAll')}</div>}
              {!loading && summaries.length > 0 && visibleStores.length === 0 && <div className={styles.empty}>{tr('stores.emptySearch')}</div>}
            </div>
          </div>

          {(selection.missingStores.length > 0 || selection.noSplitStores.length > 0) && (
            <div className={styles.banners}>
              {selection.missingStores.length > 0 && (
                <div className={classNames(styles.banner, styles.bannerWarn)} role="status">
                  <WarningOutlined className={styles.bannerIcon} aria-hidden="true" />
                  <span className={styles.bannerText}><b>{missingLead}</b>{tr('banner.missingTail')}</span>
                  <button type="button" className={styles.smallButton} disabled={exporting} onClick={excludeUnreliable}>
                    {selection.missingStores.length === 1 ? tr('banner.excludeOne') : tr('banner.excludeMany', { count: selection.missingStores.length })}
                  </button>
                </div>
              )}
              {selection.noSplitStores.length > 0 && (
                <div className={classNames(styles.banner, styles.bannerInfo)} role="status">
                  <InfoCircleOutlined className={styles.bannerIcon} aria-hidden="true" />
                  <span className={styles.bannerText}><b>{noSplitLead}</b>{tr('banner.noSplitTail')}</span>
                </div>
              )}
            </div>
          )}

          <div className={styles.legend}>
            <span>{tr('stores.colMix')}</span>
            <span className={styles.legendItem}><i className={classNames(styles.swatch, styles.swatchCard)} aria-hidden="true" />{tr('columns.card')}</span>
            <span className={styles.legendItem}><i className={classNames(styles.swatch, styles.swatchCash)} aria-hidden="true" />{tr('columns.cash')}</span>
            <span className={styles.legendItem}><i className={classNames(styles.swatch, styles.swatchOther)} aria-hidden="true" />{tr('stores.legendOther')}</span>
          </div>
        </section>

        {/* ---------- 右：当前分店逐日明细 ---------- */}
        <section className={classNames(styles.panel, styles.panelDaily)} aria-label={tr('daily.region')} aria-busy={loading}>
          <div className={classNames(styles.progress, loading && styles.progressOn)} aria-hidden="true" />
          <div className={styles.panelHeader}>
            <div className={styles.dailyTitle}>
              <i className={classNames(styles.dot, styles.dotPurple)} aria-hidden="true" />
              <h2>{focus ? focus.branchName : showLoadingText ? tr('state.loading') : tr('daily.empty')}</h2>
              {focus && <span className={styles.focusCode}>{focus.branchCode}</span>}
              {focus && <span className={styles.muted}>{tr('daily.subtitle', { monthLabel, days: focus.rows.length })}</span>}
            </div>
            {focus && (
              <span className={styles.panelNote}>
                {excluded.has(focus.branchCode) ? tr('daily.notIncluded') : tr('daily.note')}
              </span>
            )}
          </div>

          <div className={styles.dailyBody}>
            <div role="table" aria-label={tr('daily.tableLabel')} className={styles.dailyTable}>
              <div role="row" className={classNames(styles.dayRow, styles.dayHead)}>
                <div role="columnheader" className={styles.dayDate}>{tr('columns.date')}</div>
                <div role="columnheader" className={styles.dayWeekday}>{tr('columns.weekday')}</div>
                <div role="columnheader" className={styles.dayNum}>{tr('columns.revenue')}</div>
                <div role="columnheader" className={styles.dayNum}>{tr('columns.card')}</div>
                <div role="columnheader" className={styles.dayNum}>{tr('columns.cash')}</div>
                <div role="columnheader" className={styles.dayNumLast}>{tr('columns.other')}</div>
              </div>
              {loading && !data && Array.from({ length: SKELETON_ROWS }, (_, index) => (
                <div key={index} role="row" className={styles.skeletonRow} aria-hidden="true"><span /></div>
              ))}
              {focus?.rows.map((row) => (
                <div
                  key={row.date}
                  role="row"
                  className={classNames(
                    styles.dayRow,
                    isWeekend(row.weekday) && styles.dayWeekend,
                    row.status !== 'ok' && styles.dayFlagged,
                  )}
                >
                  <div role="cell" className={styles.dayDate}>{row.date.slice(5)}</div>
                  <div role="cell" className={styles.dayWeekday}>{weekdayLabels[row.weekday]}</div>
                  <div role="cell" className={styles.dayNum} title={row.status === 'missing' ? tr('daily.missingTitle') : undefined}>
                    <div className={styles.revenueCell}>
                      {row.revenue !== null && (
                        <i className={styles.revenueBar} style={{ width: `${((row.revenue / maxFocusRevenue) * 100).toFixed(1)}%` }} aria-hidden="true" />
                      )}
                      <span>{formatAmount(row.revenue)}</span>
                      {row.status === 'missing' && <span className={styles.srOnly}>{tr('daily.missingTitle')}</span>}
                    </div>
                  </div>
                  <div role="cell" className={styles.dayNum} title={row.status === 'noSplit' ? tr('daily.noSplitTitle') : undefined}>
                    {formatAmount(row.card)}
                    {row.status === 'noSplit' && <span className={styles.srOnly}>{tr('daily.noSplitTitle')}</span>}
                  </div>
                  <div role="cell" className={styles.dayNum} title={row.status === 'noSplit' ? tr('daily.noSplitTitle') : undefined}>{formatAmount(row.cash)}</div>
                  <div role="cell" className={styles.dayNumLast} title={row.status === 'noSplit' ? tr('daily.noSplitTitle') : undefined}>{formatAmount(row.other)}</div>
                </div>
              ))}
              {focus && (
                <div role="row" className={classNames(styles.dayRow, styles.dayTotal)}>
                  <div role="cell" className={styles.dayTotalLabel}>{tr('daily.totalLabel', { ...monthParams })}</div>
                  <div role="cell" className={styles.dayNum}>{formatAmount(focusTotals?.revenue ?? null)}</div>
                  <div role="cell" className={styles.dayNum}>{formatAmount(focusTotals?.card ?? null)}</div>
                  <div role="cell" className={styles.dayNum}>{formatAmount(focusTotals?.cash ?? null)}</div>
                  <div role="cell" className={styles.dayNumLast}>{formatAmount(focusTotals?.other ?? null)}</div>
                </div>
              )}
              {!loading && data && !focus && <div className={styles.empty}>{tr('daily.noStores')}</div>}
              {!loading && focus && focus.rows.length === 0 && <div className={styles.empty}>{tr('footer.noData')}</div>}
            </div>
          </div>
          {focus && (focus.missingDates.length > 0 || focus.noSplitDates.length > 0) && (
            <div className={styles.dailyLegend}>
              {focus.missingDates.length > 0 && (
                <span>{tr('daily.legendMissing', { count: focus.missingDates.length, dates: formatDayList(focus.missingDates) })}</span>
              )}
              {focus.noSplitDates.length > 0 && (
                <span>{tr('daily.legendNoSplit', { count: focus.noSplitDates.length, dates: formatDayList(focus.noSplitDates) })}</span>
              )}
            </div>
          )}
        </section>
      </div>

      {/* ---------- 底部下载栏 ---------- */}
      <footer className={styles.footer} aria-label={tr('footer.region')}>
        {exportState.kind === 'running' && (
          <div className={styles.runStrip} role="status">
            <LoadingOutlined spin className={styles.runIcon} aria-hidden="true" />
            <span className={styles.runText}>{progressText}</span>
            <div className={styles.runTrack} aria-hidden="true">
              <i style={{ width: `${Math.round((progress?.percent ?? 0) * 100)}%` }} />
            </div>
            <span className={styles.runPercent}>{Math.round((progress?.percent ?? 0) * 100)}%</span>
            {progress?.stage !== 'finalize' && (
              <button type="button" className={styles.linkButton} onClick={() => exportAbortRef.current?.abort()}>{tr('progress.cancel')}</button>
            )}
          </div>
        )}
        {exportState.kind === 'interrupted' && (
          <div className={styles.interrupted} role="alert">
            <span className={styles.interruptedMessage}>
              <CloseCircleOutlined className={styles.interruptedIcon} aria-hidden="true" />
              <span>
                <b>
                  {exportState.stage === 'store'
                    ? tr('interrupted.storeFailed', { name: exportState.failedName, reason: exportState.reason, done: exportState.done, total: exportState.total })
                    : tr('interrupted.finalizeFailed', { reason: exportState.reason })}
                </b>
                {tr('interrupted.kept', { done: exportState.done })}
              </span>
            </span>
            <span className={styles.interruptedActions}>
              <button type="button" className={styles.primarySmall} onClick={retryUnfinished}>
                {exportState.pending > 0 ? tr('interrupted.retry', { count: exportState.pending }) : tr('interrupted.retryFile')}
              </button>
              {exportState.done > 0 && exportState.pending > 0 && (
                <button type="button" className={styles.smallButton} onClick={downloadFinished}>
                  {tr('interrupted.downloadDone', { count: exportState.done })}
                </button>
              )}
              <button type="button" className={styles.linkButton} onClick={() => setExportState({ kind: 'idle' })}>{tr('interrupted.discard')}</button>
            </span>
          </div>
        )}
        <div className={styles.footerMain}>
          <div className={styles.footerText}>
            <span className={styles.summaryLine}>{summaryLine}</span>
            {selection.storeCount > 0 && <span className={styles.fileLine}>{tr('footer.fileLine', { fileName })}</span>}
            {footerNotes.map((note) => <span key={note} className={styles.footerNote}>{note}</span>)}
          </div>
          {exportState.kind === 'interrupted' ? (
            <span className={styles.muted}>{tr('footer.monthData', { ...monthParams })}</span>
          ) : (
            <div className={styles.footerActions}>
              <div role="group" aria-label={tr('footer.formatGroup')} className={styles.segmented}>
                <button type="button" className={classNames(styles.segment, format === 'xlsx' && styles.segmentOn)} aria-pressed={format === 'xlsx'} disabled={exporting} onClick={() => setFormat('xlsx')}>
                  {tr('footer.formatXlsx')}
                </button>
                <button type="button" className={classNames(styles.segment, format === 'csv' && styles.segmentOn)} aria-pressed={format === 'csv'} disabled={exporting} onClick={() => setFormat('csv')}>
                  {tr('footer.formatCsv')}
                </button>
              </div>
              {/* 用原生按钮并用 aria-disabled 表示忙碌：antd Button 的 loading 会吞掉点击，导出中也要保持可聚焦。 */}
              <button
                type="button"
                className={classNames(styles.primary, exporting && styles.primaryBusy)}
                disabled={!canDownload && !exporting}
                aria-disabled={exporting || undefined}
                aria-busy={exporting || undefined}
                onClick={() => { void handleDownload() }}
              >
                {exporting ? <LoadingOutlined spin aria-hidden="true" /> : <DownloadOutlined aria-hidden="true" />}
                {exporting ? tr('footer.generating') : selection.storeCount > 0 ? tr('footer.download', { ...monthParams }) : tr('footer.downloadIdle')}
              </button>
            </div>
          )}
        </div>
      </footer>
    </div>
  )
}

/** 提示条里的分店名列表：最多列前 3 家。 */
function nameList(stores: readonly StoreSummary[], separator: string): string {
  const names = stores.slice(0, 3).map((store) => store.branchName).join(separator)
  return stores.length > 3 ? `${names}…` : names
}

interface StoreListRowProps {
  store: StoreSummary
  checked: boolean
  focused: boolean
  disabled: boolean
  tr: (key: string, params?: Record<string, string | number>) => string
  formatDayList: (dates: readonly string[]) => string
  onToggle: () => void
  onFocus: () => void
}

/** 左栏一行：勾选 + 分店（点名称预览逐日）+ 营业额 / 刷卡 / 现金 + 支付构成条 + 缺数标记。 */
function StoreListRow({ store, checked, focused, disabled, tr, formatDayList, onToggle, onFocus }: StoreListRowProps) {
  const display = displayTotals(store.totals)
  const mix = compositionOf(store.totals)
  const mixLabel = mix
    ? tr('stores.mixLabel', { card: mix.card.toFixed(1), cash: mix.cash.toFixed(1), other: mix.other.toFixed(1) })
    : tr('stores.mixNone')
  return (
    <div role="row" className={classNames(styles.storeRow, styles.bodyRow, focused && styles.rowFocus)}>
      <div role="cell" className={classNames(styles.cellCheck, focused && styles.cellEdge)}>
        <input
          type="checkbox"
          checked={checked}
          disabled={disabled}
          aria-label={tr('stores.checkLabel', { name: store.branchName })}
          onChange={onToggle}
        />
      </div>
      <div role="cell" className={styles.cellName}>
        <button type="button" className={styles.nameButton} aria-pressed={focused} onClick={onFocus}>
          <span className={classNames(styles.storeName, !checked && styles.storeNameOff, focused && styles.storeNameFocus)}>{store.branchName}</span>
          <small className={styles.storeMeta}>
            <span>{store.branchCode}</span>
            {store.missingDates.length > 0 && (
              <span className={styles.tagWarn} title={formatDayList(store.missingDates)}>
                <WarningOutlined aria-hidden="true" />{tr('stores.tagMissing', { count: store.missingDates.length })}
              </span>
            )}
            {store.noSplitDates.length > 0 && (
              <span className={styles.tagNote} title={tr('stores.tagNoSplitTitle', { dates: formatDayList(store.noSplitDates) })}>
                {tr('stores.tagNoSplit', { count: store.noSplitDates.length })}
              </span>
            )}
          </small>
        </button>
      </div>
      <div role="cell" className={classNames(styles.cellNum, styles.cellStrong)}>{formatDollars(display.revenue)}</div>
      <div role="cell" className={styles.cellNum}>{formatDollars(display.card)}</div>
      <div role="cell" className={styles.cellNum}>{formatDollars(display.cash)}</div>
      <div role="cell" className={styles.cellMix}>
        <div role="img" aria-label={mixLabel} title={mix ? undefined : EMPTY_CELL} className={styles.mixTrack}>
          {mix && (
            <>
              <i className={styles.mixCard} style={{ width: `${mix.card.toFixed(2)}%` }} />
              <i className={styles.mixCash} style={{ width: `${mix.cash.toFixed(2)}%` }} />
              <i className={styles.mixOther} style={{ width: `${mix.other.toFixed(2)}%` }} />
            </>
          )}
        </div>
      </div>
    </div>
  )
}

export default MonthlyDailySalesDownloadPage
