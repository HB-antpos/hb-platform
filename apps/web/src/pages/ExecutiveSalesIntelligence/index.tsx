import { ClockCircleOutlined, RightOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Segmented, Skeleton } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuthStore } from '../../store/auth'
import { reportPeriod, type DateSelection } from './ReportWorkbench/logic'
import { ReportControls, useReportText } from './ReportWorkbench/ReportControls'
import { useReportQuery } from './ReportWorkbench/useReportQuery'
import CumulativeRevenuePanel from './RevenueReport/CumulativeRevenuePanel'
import HourlyCumulativeTable, { type HourlyTableMode } from './RevenueReport/HourlyCumulativeTable'
import RevenueWeeklyHierarchy from './RevenueReport/RevenueWeeklyHierarchy'
import {
  FULL_DAY_CUTOFF_HOUR,
  alignBranchesToCutoff,
  alignRangeBranchesToCutoff,
  alignRangeHourlyRows,
  alignRangeWeeklyToCutoff,
  alignWeeklyNodesToCutoff,
  buildHourlySeries,
  filterHourlyRowsByBranch,
  formatHourLabel,
  formatLocalClockTime,
  getDisplayCutoffHour,
  groupHourlySeriesByBranch,
  parseHourKey,
  referenceClockHour,
  resolveDefaultCutoff,
  resolveEffectiveCutoff,
  scopeWeeklyToBranch,
  sydneyTodayKey,
} from './RevenueReport/hourlyCumulative'
import {
  aggregateHourlyRows,
  buildRevenueOverviewSearch,
  buildSalesDetailPath,
  formatAud,
  formatInteger,
  getRevenueSummary,
  getRevenueTrend,
  makeRevenueQueryScope,
  parseRevenueOverviewSearch,
  sortRevenueBranchesByRevenue,
} from './RevenueReport/logic'
import {
  fetchRevenueReportSnapshot,
  type RevenueReportSnapshot,
} from './RevenueReport/revenueReportService'
import cumulativeStyles from './RevenueReport/cumulative.module.css'
import styles from './styles.module.css'

const INITIAL_BRANCH_COUNT = 8

function normalizeCode(value: string | null | undefined) {
  return value?.trim().toLocaleLowerCase('en-AU') ?? ''
}

export default function ExecutiveSalesIntelligence() {
  const text = useReportText()
  const navigate = useNavigate()
  const location = useLocation()
  const { active } = useKeepAliveContext()
  const access = useAuthStore(state => state.access)
  const currentUser = useAuthStore(state => state.currentUser)
  const initialQuery = useRef(parseRevenueOverviewSearch(location.search)).current
  const [selection, setSelection] = useState<DateSelection>(initialQuery.selection)
  const [selectedBranch, setSelectedBranch] = useState<{ branchCode: string; branchName: string } | null>(
    initialQuery.branchCode ? { branchCode: initialQuery.branchCode, branchName: initialQuery.branchCode } : null,
  )
  const [showAllBranches, setShowAllBranches] = useState(false)
  const [refresh, setRefresh] = useState(0)
  // 单日累计对比：null 表示默认截止（今天 = 最近完整整点，历史日期 = 全天）。
  const [selectedCutoffHour, setSelectedCutoffHour] = useState<number | null>(null)
  const [hourlyMode, setHourlyMode] = useState<HourlyTableMode>('cumulative')

  const managedStoreCodes = useMemo(() => {
    // 销售报表按全部关联分店读取，普通关联分店也可查看销售数据。
    const codes = access.visibleStoreCodes()
    return codes == null
      ? null
      : [...new Set(codes.map(code => code.trim()).filter(Boolean))].sort((left, right) => left.localeCompare(right))
  }, [access])
  const period = useMemo(() => reportPeriod(selection), [selection])
  const periodKey = `${period.startDate}:${period.endDate}:${period.compareStartDate ?? '-'}:${period.compareEndDate ?? '-'}:${period.compareMode}`
  const queryScope = makeRevenueQueryScope(currentUser?.userGUID, managedStoreCodes)
  const hasStoreScope = Boolean(currentUser) && (managedStoreCodes == null || managedStoreCodes.length > 0)

  // 时段与周层级也取授权范围全部门店：分店排行要按每家店的小时数据对齐截止整点，
  // 选中门店、点选截止整点都在前端筛选与重算，页面各区块即时联动，不再重新请求。
  const reportQuery = useReportQuery<RevenueReportSnapshot>(
    `revenue:whole-page:${queryScope}:${periodKey}`,
    signal => fetchRevenueReportSnapshot(period, managedStoreCodes, managedStoreCodes, signal),
    { active, enabled: hasStoreScope, refresh, metricId: 'revenue-whole-page' },
  )
  const selectedBranchCode = selectedBranch?.branchCode ?? null
  const allHourlyRows = reportQuery.data?.hourly
  const scopedHourlyRows = useMemo(
    () => allHourlyRows ? filterHourlyRowsByBranch(allHourlyRows, selectedBranchCode) : undefined,
    [allHourlyRows, selectedBranchCode],
  )
  const branchQuery = { ...reportQuery, data: reportQuery.data?.branches }

  useEffect(() => {
    if (!active) return
    const expected = buildRevenueOverviewSearch(selection, selectedBranch?.branchCode ?? null)
    if (location.search === expected) return
    const incoming = parseRevenueOverviewSearch(location.search)
    setSelection(incoming.selection)
    setSelectedBranch(incoming.branchCode ? { branchCode: incoming.branchCode, branchName: incoming.branchCode } : null)
  }, [active, location.search])

  const rawBranches = useMemo(
    () => sortRevenueBranchesByRevenue(branchQuery.data ?? []),
    [branchQuery.data],
  )
  // 核心 KPI、排行和周层级同源于 StoreSales；总体 comparePeriodPending 也可能只由小时缺口触发。
  const coreComparePending = selection.compare && reportQuery.data?.weeklyComparePending === true
  const hourlyCurrentPending = reportQuery.data?.hourlyCurrentPending === true
  const hourlyComparePending = selection.compare && reportQuery.data?.hourlyComparePending === true
  const weeklyComparePending = selection.compare && reportQuery.data?.weeklyComparePending === true
  const coreCompareAvailable = selection.compare && !coreComparePending
  const hourlyCompareAvailable = selection.compare && !hourlyComparePending
  const selectedDate = selection.startDate === selection.endDate ? selection.startDate : null
  const todayKey = sydneyTodayKey()
  const statisticsLastSuccessfulAtUtc = reportQuery.data?.statisticsLastSuccessfulAtUtc
  const defaultCutoff = useMemo(
    () => selectedDate && selection.compare
      ? resolveDefaultCutoff({ selectedDate, todayKey, statisticsCompletedAtUtc: statisticsLastSuccessfulAtUtc })
      : null,
    [selectedDate, selection.compare, todayKey, statisticsLastSuccessfulAtUtc],
  )
  const seriesByBranch = useMemo(() => groupHourlySeriesByBranch(allHourlyRows ?? []), [allHourlyRows])
  const allStoreSeries = useMemo(() => buildHourlySeries(allHourlyRows ?? []), [allHourlyRows])
  const scopeSeries = useMemo(() => buildHourlySeries(scopedHourlyRows ?? []), [scopedHourlyRows])
  // 分时统计不完整时不能对齐：缺店的小时会被当成 0，排名与同比都会失真，与移动端一样回退为整天口径。
  const cumulativeActive = defaultCutoff !== null && hourlyCompareAvailable && !hourlyCurrentPending
    && allHourlyRows !== undefined && !reportQuery.error
  const effectiveCutoffHour = cumulativeActive && defaultCutoff
    ? resolveEffectiveCutoff(selectedCutoffHour, defaultCutoff.cutoffHour)
    : FULL_DAY_CUTOFF_HOUR
  // 今天总要对齐到最近完整整点；历史日期默认整天，用户点选某个整点后才对齐。
  const alignToCutoff = cumulativeActive && effectiveCutoffHour < FULL_DAY_CUTOFF_HOUR
  const cutoffLabel = formatHourLabel(getDisplayCutoffHour(allStoreSeries, effectiveCutoffHour))
  const liveClock = formatLocalClockTime(statisticsLastSuccessfulAtUtc)
  const compareWeekday = period.compareStartDate ? new Date(`${period.compareStartDate}T00:00:00Z`).getUTCDay() : null
  const compareDayLabel = period.compareStartDate && compareWeekday !== null
    ? `${period.compareStartDate} ${text(['周日', '周一', '周二', '周三', '周四', '周五', '周六'][compareWeekday]!, ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][compareWeekday]!)}`
    : ''
  const rankingCompareAvailable = alignToCutoff ? hourlyCompareAvailable : coreCompareAvailable
  const weeklyCompareAvailable = alignToCutoff ? hourlyCompareAvailable : selection.compare && !weeklyComparePending

  // 本周、本月等最后一天是今天的多日区间：今天换成截至最近完整整点的累计，其余日期仍按全天比较。
  const lastDay = reportQuery.data?.lastDay ?? null
  const lastDayDate = lastDay?.date?.slice(0, 10) ?? null
  const rangeEndsToday = !selectedDate && selection.endDate === todayKey && selection.compare
  const rangeCutoff = useMemo(
    () => rangeEndsToday
      ? resolveDefaultCutoff({ selectedDate: todayKey, todayKey, statisticsCompletedAtUtc: statisticsLastSuccessfulAtUtc })
      : null,
    [rangeEndsToday, todayKey, statisticsLastSuccessfulAtUtc],
  )
  // 与单日一样 fail-closed：缺统计时间、旧后端没有最后一天数据、分时未齐时都不对齐。
  const rangeAlignActive = Boolean(rangeCutoff?.live) && lastDay !== null && lastDayDate === selection.endDate
    && hourlyCompareAvailable && !hourlyCurrentPending && !reportQuery.error
  const rangeCutoffHour = rangeAlignActive && rangeCutoff ? rangeCutoff.cutoffHour : FULL_DAY_CUTOFF_HOUR
  const rangeCutoffLabel = formatHourLabel(rangeCutoffHour)
  const rangeDayCount = Math.round((Date.parse(selection.endDate) - Date.parse(selection.startDate)) / 86_400_000) + 1
  const lastDaySeriesByBranch = useMemo(() => groupHourlySeriesByBranch(lastDay?.hourly ?? []), [lastDay])
  const alignedHourlyRows = useMemo(() => {
    if (!scopedHourlyRows || !rangeAlignActive || !lastDay) return scopedHourlyRows
    return alignRangeHourlyRows(scopedHourlyRows, filterHourlyRowsByBranch(lastDay.hourly, selectedBranchCode), rangeCutoffHour)
  }, [lastDay, rangeAlignActive, rangeCutoffHour, scopedHourlyRows, selectedBranchCode])
  const hourlyQuery = { ...reportQuery, data: alignedHourlyRows }

  const branches = useMemo(() => {
    if (alignToCutoff) return alignBranchesToCutoff(rawBranches, seriesByBranch, effectiveCutoffHour)
    if (rangeAlignActive && lastDay) return alignRangeBranchesToCutoff(rawBranches, lastDay.branches, lastDaySeriesByBranch, rangeCutoffHour)
    return rawBranches
  }, [alignToCutoff, effectiveCutoffHour, lastDay, lastDaySeriesByBranch, rangeAlignActive, rangeCutoffHour, rawBranches, seriesByBranch])
  const rawWeekly = reportQuery.data?.weekly
  const weeklyData = useMemo(() => {
    if (!rawWeekly) return undefined
    const aligned = alignToCutoff
      ? alignWeeklyNodesToCutoff(rawWeekly, seriesByBranch, effectiveCutoffHour)
      : rangeAlignActive && lastDayDate
        ? alignRangeWeeklyToCutoff(rawWeekly, lastDayDate, lastDaySeriesByBranch, rangeCutoffHour)
        : rawWeekly
    return scopeWeeklyToBranch(aligned, selectedBranchCode)
  }, [alignToCutoff, effectiveCutoffHour, lastDayDate, lastDaySeriesByBranch, rangeAlignActive, rangeCutoffHour, rawWeekly, selectedBranchCode, seriesByBranch])
  const weeklyQuery = { ...reportQuery, data: weeklyData }
  const alignmentUnavailableReason = (selectedDate === todayKey || rangeEndsToday) && selection.compare && reportQuery.data
    && !cumulativeActive && !rangeAlignActive
    ? (selectedDate ? defaultCutoff : rangeCutoff) === null
      ? text('尚未取得今天的统计发布时间', 'today’s statistics time is not available yet')
      : !selectedDate && lastDayDate !== selection.endDate
        ? text('后端尚未提供今天的分时数据', 'today’s hourly data is not available from the server yet')
        : text('今天的分时统计尚未齐全', 'today’s hourly statistics are incomplete')
    : null
  // 拿不到截止整点时，单日今天的旧时段表按 UTC+10 墙钟推断：之后的小时未到、当前小时进行中。
  const fallbackClockHour = selectedDate === todayKey && !cumulativeActive ? referenceClockHour() : null

  useEffect(() => {
    // 换日期后回到默认截止整点，避免沿用上一天点选的时刻。
    setSelectedCutoffHour(null)
  }, [periodKey])
  useEffect(() => {
    if (!selectedBranch || !branchQuery.data) return
    const match = branchQuery.data.find(branch => normalizeCode(branch.branchCode) === normalizeCode(selectedBranch.branchCode))
    if (match && match.branchName !== selectedBranch.branchName) {
      setSelectedBranch({ branchCode: match.branchCode, branchName: match.branchName })
    }
  }, [branchQuery.data, selectedBranch])
  const summary = useMemo(
    () => getRevenueSummary(branches, selectedBranch?.branchCode ?? null, rankingCompareAvailable),
    [branches, selectedBranch?.branchCode, rankingCompareAvailable],
  )
  const hourlyRows = useMemo(
    () => aggregateHourlyRows(hourlyQuery.data ?? [], hourlyCompareAvailable),
    [hourlyQuery.data, hourlyCompareAvailable],
  )
  // 回退态：最后一个有本期数据的小时。之后到当前整点之间本期为 0，多半是统计还没跑到，不能当成 −100%。
  const fallbackLastDataHour = useMemo(() => {
    if (fallbackClockHour === null) return null
    let last: number | null = null
    for (const row of hourlyRows) {
      const hour = parseHourKey(row.hour)
      if (hour !== null && row.revenue > 0 && (last === null || hour > last)) last = hour
    }
    return last
  }, [fallbackClockHour, hourlyRows])
  const visibleBranches = showAllBranches ? branches : branches.slice(0, INITIAL_BRANCH_COUNT)
  const hasBranchMetrics = branchQuery.data !== undefined && branches.length > 0 && !branchQuery.error
    && (!selectedBranch || branches.some(branch => normalizeCode(branch.branchCode) === normalizeCode(selectedBranch.branchCode)))
  const allLoading = reportQuery.loading
  const anySlow = reportQuery.slow
  const scopeLabel = selectedBranch?.branchName || text('全部门店', 'All branches')
  const kpiCaption = rangeAlignActive
    ? text(`${scopeLabel} · 今天截至 ${rangeCutoffLabel}`, `${scopeLabel} · today to ${rangeCutoffLabel}`)
    : scopeLabel
  const localizeTrend = (trend: ReturnType<typeof getRevenueTrend>) => ({
    ...trend,
    text: trend.text === 'new' ? text('新增', 'New') : trend.text,
  })
  const revenueTrend = localizeTrend(getRevenueTrend(hasBranchMetrics ? summary.revenue : null, hasBranchMetrics ? summary.revenueLY : null))
  const orderTrend = localizeTrend(getRevenueTrend(hasBranchMetrics ? summary.orders : null, hasBranchMetrics ? summary.ordersLY : null))
  const aovTrend = localizeTrend(getRevenueTrend(hasBranchMetrics ? summary.aov : null, hasBranchMetrics ? summary.aovLY : null))
  const signalValue = !hasStoreScope
    ? text('无可访问分店', 'No accessible branches')
    : branchQuery.error
      ? text('核心数据加载失败', 'Core data unavailable')
      : !hasBranchMetrics
        ? text('暂无业绩数据', 'No performance data')
        : !selection.compare
          ? text('自动对比已暂停', 'Comparison paused')
          : coreComparePending
            ? text('同期统计未齐', 'Comparison snapshot incomplete')
          : selectedBranch
            ? revenueTrend.text
            : text(`${summary.decliningBranches} 家门店需关注`, `${summary.decliningBranches} branches need attention`)
  const signalDetail = !hasBranchMetrics
    ? '—'
    : selection.compare
      ? coreComparePending
        ? text('本期业绩已显示；同期完成后再计算同比。', 'Current performance is shown; comparison trends will follow when ready.')
        : selectedBranch
          ? text(`订单同比 ${orderTrend.text} · 客单价同比 ${aovTrend.text}`, `Orders ${orderTrend.text} · AOV ${aovTrend.text}`)
          : text(`${branches.length - summary.decliningBranches - summary.newBranches} 家增长或持平 · ${summary.newBranches} 家无同期`, `${branches.length - summary.decliningBranches - summary.newBranches} growing or flat · ${summary.newBranches} new`)
      : text('开启自动对比后显示同期趋势', 'Enable comparison to see trends')

  const refreshAllSections = () => {
    setRefresh(value => value + 1)
  }

  const commitFilters = (nextSelection: DateSelection, nextBranch: { branchCode: string; branchName: string } | null) => {
    setSelection(nextSelection)
    setSelectedBranch(nextBranch)
    navigate({ pathname: location.pathname, search: buildRevenueOverviewSearch(nextSelection, nextBranch?.branchCode ?? null) }, { replace: true })
  }

  const selectBranch = (branchCode: string, branchName: string) => {
    const nextBranch = normalizeCode(selectedBranch?.branchCode) === normalizeCode(branchCode)
      ? null
      : { branchCode, branchName }
    commitFilters(selection, nextBranch)
  }

  const updateRange = (range: { startDate: string; endDate: string }) => {
    const nextSelection: DateSelection = { ...selection, ...range, quick: 'custom' }
    commitFilters(nextSelection, selectedBranch)
  }

  const selectCutoffHour = (hour: number) => {
    if (!defaultCutoff) return
    setSelectedCutoffHour(hour >= getDisplayCutoffHour(scopeSeries, defaultCutoff.cutoffHour) ? null : hour)
  }

  const openSalesDetail = (branchCode: string) => {
    navigate(buildSalesDetailPath(branchCode, selection))
  }

  const renderTrend = (current: number | null | undefined, previous: number | null | undefined) => {
    const trend = localizeTrend(getRevenueTrend(current, previous))
    return <span className={`${styles.trend} ${styles[trend.tone]}`}>{trend.text}</span>
  }

  return (
    <main className={styles.pageContainer}>
      <header className={styles.pageHeader}>
        <div>
          <h1>{text('营业额报告', 'Revenue report')}</h1>
          <p>{text('关键业绩、门店排名与时段表现集中在一个决策视图。', 'Revenue, branch rankings and hourly performance in one decision view.')}</p>
        </div>
        <div className={styles.scopeContext} aria-live="polite">
          <span>{text('当前范围', 'Current scope')}</span>
          <strong>{scopeLabel}</strong>
          {selectedBranch && (
            <button type="button" onClick={() => commitFilters(selection, null)}>
              {text('清除门店', 'Clear branch')}
            </button>
          )}
        </div>
      </header>

      <ReportControls
        value={selection}
        onChange={nextSelection => commitFilters(nextSelection, selectedBranch)}
        onRefresh={refreshAllSections}
        loading={allLoading}
      />

      {!hasStoreScope && currentUser && (
        <Alert className={styles.scopeAlert} type="info" showIcon
          message={text('当前账号没有可访问的分店范围', 'This account has no accessible branch scope')} />
      )}

      <div className={styles.queryStatus} role="status" aria-live="polite">
        {!hasStoreScope
          ? text('未发送营业额查询。', 'No revenue query was sent.')
          : anySlow
          ? text('查询已超过 3 秒，整页统计快照仍在读取。', 'The query has taken over 3 seconds. The whole-page snapshot is still loading.')
          : reportQuery.cached
            ? text('已显示 30 秒内缓存，可手动刷新。', 'Showing the 30-second cache. Refresh to query again.')
            : reportQuery.durationMs != null
              ? text(`整页快照返回 ${Math.round(reportQuery.durationMs)} ms`, `Whole-page snapshot returned in ${Math.round(reportQuery.durationMs)} ms`)
              : text('门店、时段与周层级从同一统计快照加载。', 'Branches, hourly data and weekly hierarchy load from one snapshot.')}
      </div>

      {coreComparePending && (
        <Alert className={styles.scopeAlert} type="info" showIcon
          message={text('同期门店统计尚未齐全；本期业绩正常显示，同比暂显示 —。', 'Branch comparison statistics are incomplete. Current performance remains visible and comparisons show —.')} />
      )}
      {alignmentUnavailableReason && (
        // 拿不到截止整点时只能按已入账部分对比去年全天，必须明确告知同比会偏低。
        <Alert className={styles.scopeAlert} type="info" showIcon
          message={text(`今天暂未对齐到整点（${alignmentUnavailableReason}）；当前同比是今天已入账部分对比去年全天，会明显偏低。`,
            `Today is not aligned to an hour yet (${alignmentUnavailableReason}); comparisons use today so far against last year's full day and read low.`)} />
      )}
      {reportQuery.data && reportQuery.snapshot?.statisticMessage && (
        // 快照完整但个别日期对账未通过：数据照常显示，只提示日期。
        <Alert className={styles.scopeAlert} type="warning" showIcon message={reportQuery.snapshot.statisticMessage} />
      )}

      {cumulativeActive && defaultCutoff ? (
        <CumulativeRevenuePanel
          scopeLabel={scopeLabel}
          compareLabel={compareDayLabel}
          series={scopeSeries}
          cutoff={defaultCutoff}
          effectiveCutoffHour={effectiveCutoffHour}
          liveClock={liveClock}
          branchCounts={selectedBranch ? null : {
            ahead: branches.filter(branch => branch.revenueLY > 0 && branch.revenue > branch.revenueLY).length,
            behind: branches.filter(branch => branch.revenueLY > 0 && branch.revenue < branch.revenueLY).length,
          }}
          userPickedCutoff={selectedCutoffHour !== null && selectedCutoffHour < defaultCutoff.cutoffHour}
          onSelectCutoff={selectCutoffHour}
          onResetCutoff={() => setSelectedCutoffHour(null)}
        />
      ) : (
      <section className={styles.summaryGrid} aria-label={text('核心业绩', 'Key metrics')}>
        <MetricCard label={text('销售额', 'Revenue')} value={formatAud(hasBranchMetrics ? summary.revenue : null)}
          previous={coreCompareAvailable && hasBranchMetrics ? `${text('同期', 'Previous')} ${formatAud(summary.revenueLY)}` : '—'}
          trend={revenueTrend} caption={kpiCaption} loading={branchQuery.loading && !branchQuery.data} />
        <MetricCard label={text('订单数', 'Orders')} value={formatInteger(hasBranchMetrics ? summary.orders : null)}
          previous={coreCompareAvailable && hasBranchMetrics ? `${text('同期', 'Previous')} ${formatInteger(summary.ordersLY)}` : '—'}
          trend={orderTrend} caption={kpiCaption} loading={branchQuery.loading && !branchQuery.data} />
        <MetricCard label={text('客单价', 'AOV')} value={formatAud(hasBranchMetrics ? summary.aov : null, 2)}
          previous={coreCompareAvailable && hasBranchMetrics ? `${text('同期', 'Previous')} ${formatAud(summary.aovLY, 2)}` : '—'}
          trend={aovTrend} caption={text('销售额 ÷ 订单数', 'Revenue ÷ orders')} loading={branchQuery.loading && !branchQuery.data} />
        <article className={styles.metricCard}>
          <span className={styles.metricLabel}>{text('经营信号', 'Business signal')}</span>
          {branchQuery.loading && !branchQuery.data ? <Skeleton.Button active block /> : (
            <>
              <strong className={`${styles.metricValue} ${styles[revenueTrend.tone]}`}>
                {signalValue}
              </strong>
              <div className={styles.metricComparison}>{signalDetail}</div>
              <small>{hasBranchMetrics
                ? selectedBranch
                  ? text('已联动门店与时段数据', 'Branch and hourly data linked')
                  : text('选择门店查看单店表现', 'Select a branch for its performance')
                : text('请检查范围或刷新重试', 'Check the scope or refresh to retry')}</small>
            </>
          )}
        </article>
      </section>
      )}

      <section className={styles.analysisGrid} aria-label={text('门店与时段分析', 'Branch and hourly analysis')}>
        <article className={styles.panel}>
          <div className={styles.panelHeader}>
            <div>
              <h2>{text('分店表现', 'Branch performance')}</h2>
              <p>{selectedBranch ? text(`已选择 ${selectedBranch.branchName}`, `${selectedBranch.branchName} selected`) : text('选择门店联动 KPI 与时段表现', 'Select a branch to link KPIs and hourly data')}</p>
            </div>
            <Button type="link" aria-expanded={showAllBranches} onClick={() => setShowAllBranches(value => !value)}>
              {showAllBranches ? text('收起排名', 'Show less') : text(`查看全部 ${branches.length} 家`, `View all ${branches.length}`)}
            </Button>
          </div>
          {alignToCutoff && (
            <div className={cumulativeStyles.alignBar}>
              <span className={cumulativeStyles.alignPill}><ClockCircleOutlined aria-hidden="true" />
                {text(`截至 ${cutoffLabel} · 对比去年同时刻`, `To ${cutoffLabel} · vs same time LY`)}</span>
              <span>{text('排名与同比按同一整点计算，不再拿今天半天比去年全天', 'Ranks and YoY use the same cutoff hour for both years')}</span>
            </div>
          )}
          {rangeAlignActive && (
            <div className={cumulativeStyles.alignBar}>
              <span className={cumulativeStyles.alignPill}><ClockCircleOutlined aria-hidden="true" />
                {text(`今天截至 ${rangeCutoffLabel} · 其余 ${rangeDayCount - 1} 天全天`, `Today to ${rangeCutoffLabel} · other ${rangeDayCount - 1} days in full`)}</span>
              <span>{text('今天与去年对应日按同一整点比较，其余日期按全天比较', 'Today and its matching day last year use the same cutoff hour; other days compare in full')}</span>
            </div>
          )}
          {branchQuery.error ? (
            <SectionError message={branchQuery.error} text={text} onRetry={refreshAllSections} />
          ) : branchQuery.loading && !branchQuery.data ? (
            <TableSkeleton />
          ) : visibleBranches.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={text('所选范围暂无分店营业额', 'No branch revenue in this range')} />
          ) : (
            <div className={styles.tableScroll}>
              <table className={`${styles.dataTable} ${styles.branchTable}`}>
                <thead>
                  <tr>
                    <th scope="col">{text('排名', 'Rank')}</th>
                    <th scope="col">{text('分店', 'Branch')}</th>
                    <th scope="col"><ColumnPairLabel label={text('销售额', 'Revenue')} text={text} aligned={alignToCutoff} /></th>
                    <th scope="col">{text('销售同比', 'Revenue YoY')}</th>
                    <th scope="col"><ColumnPairLabel label={text('订单数', 'Orders')} text={text} aligned={alignToCutoff} /></th>
                    <th scope="col">{text('订单同比', 'Orders YoY')}</th>
                    <th scope="col"><ColumnPairLabel label={text('客单价', 'AOV')} text={text} aligned={alignToCutoff} /></th>
                    <th scope="col">{text('客单同比', 'AOV YoY')}</th>
                    <th scope="col">{text('反查', 'Drill-down')}</th>
                  </tr>
                </thead>
                <tbody>
                  {visibleBranches.map(branch => {
                    const selected = normalizeCode(selectedBranch?.branchCode) === normalizeCode(branch.branchCode)
                    return (
                      <tr key={branch.branchCode} className={selected ? styles.selectedRow : undefined}>
                        <td className={styles.rankCell}>{String(branch.rank).padStart(2, '0')}</td>
                        <th scope="row">
                          <button type="button" className={styles.branchSelect} aria-pressed={selected}
                            onClick={() => selectBranch(branch.branchCode, branch.branchName)}>
                            <strong>{branch.branchName}</strong><small>{branch.branchCode}</small>
                          </button>
                        </th>
                        <td><ValuePair current={formatAud(branch.revenue)} previous={rankingCompareAvailable ? formatAud(branch.revenueLY) : '—'} /></td>
                        <td>{renderTrend(branch.revenue, rankingCompareAvailable ? branch.revenueLY : null)}</td>
                        <td><ValuePair current={formatInteger(branch.orderCount)} previous={rankingCompareAvailable ? formatInteger(branch.orderCountLY) : '—'} /></td>
                        <td>{renderTrend(branch.orderCount, rankingCompareAvailable ? branch.orderCountLY : null)}</td>
                        <td><ValuePair current={formatAud(branch.aov, 2)} previous={rankingCompareAvailable ? formatAud(branch.aovLY, 2) : '—'} /></td>
                        <td>{renderTrend(branch.aov, rankingCompareAvailable ? branch.aovLY : null)}</td>
                        <td>{access.canViewSalesDetail && <button type="button" className={styles.detailButton} onClick={() => openSalesDetail(branch.branchCode)}>
                          {text('销售明细', 'Sales detail')} <RightOutlined aria-hidden="true" />
                        </button>}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
          <footer className={styles.panelFooter}>
            <span>{text(`显示 ${visibleBranches.length} / ${branches.length} 家门店`, `Showing ${visibleBranches.length} of ${branches.length} branches`)}</span>
            <span>{alignToCutoff
              ? text(`按截至 ${cutoffLabel} 销售额从高到低`, `Sorted by revenue to ${cutoffLabel}`)
              : rangeAlignActive
                ? text(`按销售额（今天截至 ${rangeCutoffLabel}）从高到低`, `Sorted by revenue (today to ${rangeCutoffLabel})`)
                : text('按销售额从高到低', 'Sorted by revenue')}</span>
          </footer>
        </article>

        <article className={styles.panel}>
          <div className={styles.panelHeader}>
            <div><h2>{text('时段表现', 'Hourly performance')}</h2><p>{scopeLabel} · {cumulativeActive && hourlyMode === 'cumulative'
              ? text('从开门起累计', 'Cumulative from opening')
              : text('销售密度', 'Sales density')}</p></div>
            {cumulativeActive ? (
              <Segmented size="small" value={hourlyMode} onChange={value => setHourlyMode(value as HourlyTableMode)}
                aria-label={text('时段口径', 'Hourly view')}
                options={[{ value: 'cumulative', label: text('累计', 'Cumulative') }, { value: 'perHour', label: text('逐小时', 'Per hour') }]} />
            ) : selectedBranch && <Button type="link" onClick={() => commitFilters(selection, null)}>{text('全部门店', 'All branches')}</Button>}
          </div>
          {(hourlyCurrentPending || hourlyComparePending) && (
            <Alert className={styles.sectionAlert} type="info" showIcon message={hourlyCurrentPending
              ? hourlyComparePending
                ? text('本期与同期时段统计尚未齐全；已返回的本期时段继续显示，同比暂显示 —。', 'Current and comparison hourly statistics are incomplete. Available current-hour data remains visible and comparisons show —.')
                : text('本期时段统计尚未齐全；已返回的真实时段继续显示。', 'Current hourly statistics are incomplete; available current-hour data remains visible.')
              : text('同期时段统计尚未齐全；本期时段正常显示，同比暂显示 —。', 'Hourly comparison statistics are incomplete. Current hourly data remains visible and comparisons show —.')} />
          )}
          {hourlyQuery.error ? (
            <SectionError message={hourlyQuery.error} text={text} onRetry={refreshAllSections} />
          ) : hourlyQuery.loading && !hourlyQuery.data ? (
            <TableSkeleton compact />
          ) : hourlyRows.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={text('所选范围暂无时段数据', 'No hourly data in this range')} />
          ) : cumulativeActive && defaultCutoff ? (
            <>
              <HourlyCumulativeTable series={scopeSeries} cutoff={defaultCutoff} effectiveCutoffHour={effectiveCutoffHour}
                mode={hourlyMode} liveClock={liveClock} onSelectCutoff={selectCutoffHour} />
              <footer className={styles.panelFooter}>
                <span>{text('从开门起累计，按门店本地时间；进行中的小时结束后才参与比较。', 'Totals run from opening in store local time; the hour in progress joins once it ends.')}</span>
              </footer>
            </>
          ) : (
            <div className={styles.tableScroll}>
              <table className={`${styles.dataTable} ${styles.hourlyTable}`}>
                <thead><tr>
                  <th scope="col">{text('时段', 'Hour')}</th><th scope="col">{text('本期', 'Current')}</th>
                  <th scope="col">{text('同期', 'Previous')}</th><th scope="col">{text('同比', 'YoY')}</th><th scope="col">{text('销售密度', 'Density')}</th>
                </tr></thead>
                <tbody>{hourlyRows.map(row => {
                  const hour = parseHourKey(row.hour)
                  const upcoming = fallbackClockHour !== null && hour !== null && hour > fallbackClockHour
                  const inProgress = fallbackClockHour !== null && hour === fallbackClockHour
                  const awaitingStats = fallbackClockHour !== null && hour !== null && hour < fallbackClockHour
                    && row.revenue === 0 && (fallbackLastDataHour === null || hour > fallbackLastDataHour)
                  // 没有统计时间就无法证明最后一个有数据的小时是完整的，早于当前整点时不算同比。
                  const maybePartial = fallbackClockHour !== null && hour !== null && hour === fallbackLastDataHour && hour < fallbackClockHour
                  return (
                    <tr key={row.hour} className={upcoming ? cumulativeStyles.upcomingRow : row.isPeak ? styles.peakRow : undefined}>
                      <th scope="row">{row.hour}</th>
                      <td className={styles.numeric}>{upcoming || awaitingStats
                        ? <span className={cumulativeStyles.cellWithTag}><span className={styles.mutedValue}>—</span><span className={cumulativeStyles.mutedTag}>{upcoming ? text('未到', 'Not yet') : text('待统计', 'Pending')}</span></span>
                        : inProgress || maybePartial
                          ? <span className={cumulativeStyles.cellWithTag}>
                            <span className={cumulativeStyles.liveValue}>{row.revenue > 0 ? formatAud(row.revenue) : '—'}</span>
                            <span className={cumulativeStyles.liveTag}>{inProgress ? text('进行中', 'In progress') : text('可能未统计完', 'May be incomplete')}</span>
                          </span>
                          : formatAud(row.revenue)}</td>
                      <td className={`${styles.numeric} ${styles.mutedValue}`}>{formatAud(hourlyCompareAvailable ? row.revenueLY : null)}</td>
                      <td>{upcoming || inProgress || awaitingStats || maybePartial
                        ? <span className={styles.mutedValue}>—</span>
                        : renderTrend(row.revenue, hourlyCompareAvailable ? row.revenueLY : null)}</td>
                      <td>{upcoming || awaitingStats ? null : <div className={styles.progressCell}><progress value={row.percentage} max={100}>{row.percentage}%</progress><span>{row.percentage}%</span></div>}</td>
                    </tr>
                  )
                })}</tbody>
              </table>
              {rangeAlignActive && (
                <footer className={styles.panelFooter}>
                  <span>{text(`今天 ${rangeCutoffLabel} 起的时段两期都不含今天与去年对应日，只比前 ${rangeDayCount - 1} 天。`,
                    `From ${rangeCutoffLabel}, both periods exclude today and its matching day, comparing the other ${rangeDayCount - 1} days.`)}</span>
                </footer>
              )}
            </div>
          )}
        </article>
      </section>

      <section className={styles.panel} aria-labelledby="weekly-revenue-title">
        <div className={styles.panelHeader}>
          <div><h2 id="weekly-revenue-title">{text('周业绩层级', 'Weekly performance hierarchy')}</h2>
            <p>{text('展开周与分店，再选择周、分店或日期联动上方分析。', 'Expand weeks and branches, then select a week, branch or date to update the analysis above.')}</p></div>
          <span className={styles.panelStatus}>{selection.startDate} — {selection.endDate} · {scopeLabel}{alignToCutoff
            ? text(` · 截至 ${cutoffLabel}`, ` · to ${cutoffLabel}`)
            : rangeAlignActive ? text(` · 今天截至 ${rangeCutoffLabel}`, ` · today to ${rangeCutoffLabel}`) : ''}</span>
        </div>
        {weeklyComparePending && (
          <Alert className={styles.sectionAlert} type="info" showIcon
            message={text('同期周统计尚未齐全；本期周层级正常显示，同比暂显示 —。', 'Weekly comparison statistics are incomplete. Current hierarchy remains visible and comparisons show —.')} />
        )}
        {weeklyQuery.error ? (
          <SectionError message={weeklyQuery.error} text={text} onRetry={refreshAllSections} />
        ) : weeklyQuery.loading && !weeklyQuery.data ? (
          <TableSkeleton />
        ) : !weeklyQuery.data?.length ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={text('所选范围暂无周业绩数据', 'No weekly data in this range')} />
        ) : (
          <RevenueWeeklyHierarchy data={weeklyQuery.data} compare={weeklyCompareAvailable}
            selectedBranchCode={selectedBranch?.branchCode ?? null} selectedDate={selectedDate}
            queryRange={{ startDate: selection.startDate, endDate: selection.endDate }}
            onSelectWeek={updateRange} onSelectBranch={selectBranch}
            onSelectDate={(date, branchCode, branchName) => {
              const nextSelection: DateSelection = { ...selection, startDate: date, endDate: date, quick: 'custom' }
              const nextBranch = branchCode ? { branchCode, branchName: branchName || branchCode } : selectedBranch
              commitFilters(nextSelection, nextBranch)
            }} />
        )}
      </section>
    </main>
  )
}

function MetricCard({ label, value, previous, trend, caption, loading }: {
  label: string
  value: string
  previous: string
  trend: { text: string; tone: 'positive' | 'negative' | 'neutral' }
  caption: string
  loading: boolean
}) {
  return <article className={styles.metricCard}>
    <span className={styles.metricLabel}>{label}</span>
    {loading ? <Skeleton.Button active block /> : <>
      <strong className={styles.metricValue}>{value}</strong>
      <div className={styles.metricComparison}><span>{previous}</span><span className={`${styles.trend} ${styles[trend.tone]}`}>{trend.text}</span></div>
      <small>{caption}</small>
    </>}
  </article>
}

function ColumnPairLabel({ label, text, aligned = false }: { label: string; text: (zh: string, en: string) => string; aligned?: boolean }) {
  return <span className={styles.columnPairLabel}><strong>{label}</strong><small>{aligned
    ? text('本期 / 去年同时刻', 'Current / LY same time')
    : text('本期 / 同期', 'Current / previous')}</small></span>
}

function ValuePair({ current, previous }: { current: string; previous: string }) {
  return <span className={styles.valuePair}><strong>{current}</strong><small>{previous}</small></span>
}

function SectionError({ message, text, onRetry }: { message: string; text: (zh: string, en: string) => string; onRetry: () => void }) {
  return <Alert className={styles.sectionAlert} type="warning" showIcon
    message={text('此区域加载失败', 'This section could not load')} description={message}
    action={<Button size="small" onClick={onRetry}>{text('重试', 'Retry')}</Button>} />
}

function TableSkeleton({ compact = false }: { compact?: boolean }) {
  return <div className={styles.tableSkeleton} aria-label="loading">
    {Array.from({ length: compact ? 6 : 8 }, (_, index) => <Skeleton.Input key={index} active block />)}
  </div>
}
