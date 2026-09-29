import { ClockCircleOutlined } from '@ant-design/icons'
import { useMemo } from 'react'
import { useReportText } from '../ReportWorkbench/ReportControls'
import CumulativeRevenueChart from './CumulativeRevenueChart'
import {
  FULL_DAY_CUTOFF_HOUR,
  buildCumulativeChartModel,
  formatHourLabel,
  getCumulativeTotals,
  getCutoffOptions,
  getDisplayCutoffHour,
  isLowBase,
  type CutoffResolution,
  type HourlySeries,
} from './hourlyCumulative'
import { formatAud, formatInteger, getRevenueTrend } from './logic'
import pageStyles from '../styles.module.css'
import styles from './cumulative.module.css'

type Text = (zh: string, en: string) => string

export interface CumulativeRevenuePanelProps {
  scopeLabel: string
  compareLabel: string
  series: HourlySeries
  cutoff: CutoffResolution
  effectiveCutoffHour: number
  liveClock: string | null
  /** 全部门店视图下按同一截止整点统计的领先 / 落后门店数；选中单店时为 null。 */
  branchCounts: { ahead: number; behind: number } | null
  userPickedCutoff: boolean
  onSelectCutoff: (hour: number) => void
  onResetCutoff: () => void
}

function signedAud(value: number) {
  const rounded = Math.round(value)
  return `${rounded > 0 ? '+' : rounded < 0 ? '−' : '±'}${formatAud(Math.abs(rounded))}`
}

function trendPill(current: number, previous: number, text: Text) {
  const trend = getRevenueTrend(current, previous)
  return { className: `${pageStyles.trend} ${pageStyles[trend.tone]}`, text: trend.text === 'new' ? text('新增', 'New') : trend.text }
}

export default function CumulativeRevenuePanel({
  scopeLabel,
  compareLabel,
  series,
  cutoff,
  effectiveCutoffHour,
  liveClock,
  branchCounts,
  userPickedCutoff,
  onSelectCutoff,
  onResetCutoff,
}: CumulativeRevenuePanelProps) {
  const text = useReportText()
  const chartModel = useMemo(() => buildCumulativeChartModel(series, cutoff), [series, cutoff])
  const options = useMemo(() => getCutoffOptions(series, cutoff.cutoffHour), [series, cutoff.cutoffHour])

  const header = (
    <header className={styles.header}>
      <div className={styles.headerTitle}>
        <h2 id="cumulative-revenue-title">{text('累计营业额', 'Cumulative revenue')}</h2>
        <span>{text(`对比 ${compareLabel} · 按门店本地时间`, `vs ${compareLabel} · store local time`)}</span>
      </div>
      <span className={`${styles.badge} ${cutoff.live ? styles.badgeLive : ''}`}>
        {cutoff.live
          ? text(`实时 · 统计于 ${liveClock ?? formatHourLabel(cutoff.cutoffHour)}`, `Live · updated ${liveClock ?? formatHourLabel(cutoff.cutoffHour)}`)
          : text('历史日期 · 默认整天对比', 'Past date · full-day comparison')}
      </span>
    </header>
  )

  if (!chartModel || series.firstHour === null || series.endHour === null) {
    return (
      <section className={styles.panel} aria-labelledby="cumulative-revenue-title">
        {header}
        <div className={styles.stateBox}>{text('所选范围暂无分时销售记录', 'No hourly sales in this scope')}</div>
      </section>
    )
  }

  const displayCutoff = getDisplayCutoffHour(series, effectiveCutoffHour)
  const totals = getCumulativeTotals(series, effectiveCutoffHour)
  const fullDay = getCumulativeTotals(series, FULL_DAY_CUTOFF_HOUR)
  const coversFullDay = !cutoff.live && displayCutoff >= series.endHour
  const noCompleteHour = cutoff.live && options.length === 0
  const lowBase = isLowBase(totals.compareRevenue, fullDay.compareRevenue)
  const difference = totals.revenue - totals.compareRevenue
  const headlineTrend = getRevenueTrend(totals.revenue, totals.compareRevenue)
  const headlinePill = lowBase
    ? { className: `${pageStyles.neutral}`, text: signedAud(difference) }
    : { className: pageStyles[headlineTrend.tone], text: headlineTrend.text === 'new' ? text('新增', 'New') : headlineTrend.text }
  const pillBackground = lowBase || headlineTrend.tone === 'neutral' ? '#eef2f6' : headlineTrend.tone === 'positive' ? '#eaf6ed' : '#fff0ed'
  const sameTimeZh = coversFullDay ? '去年同日' : '去年同一时刻'
  const sameTimeEn = coversFullDay ? 'the same day last year' : 'the same time last year'
  const differenceText = lowBase
    ? text('同期基数太小，改显示金额差', 'Low comparison base; showing the dollar gap')
    : difference > 0
      ? text(`比${sameTimeZh}多 ${formatAud(difference)}`, `${formatAud(difference)} ahead of ${sameTimeEn}`)
      : difference < 0
        ? text(`比${sameTimeZh}少 ${formatAud(-difference)}`, `${formatAud(-difference)} behind ${sameTimeEn}`)
        : text(`与${sameTimeZh}持平`, `Level with ${sameTimeEn}`)
  const asOf = noCompleteHour
    ? text(`${formatHourLabel(series.firstHour + 1)} 起开始对比`, `Comparison starts at ${formatHourLabel(series.firstHour + 1)}`)
    : coversFullDay
      ? text(`全天 · ${scopeLabel}`, `Full day · ${scopeLabel}`)
      : text(`截至 ${formatHourLabel(displayCutoff)} · ${scopeLabel}`, `To ${formatHourLabel(displayCutoff)} · ${scopeLabel}`)
  const reference = coversFullDay ? text('去年同日', 'LY day') : text('去年同时刻', 'LY same time')
  const aov = totals.orders > 0 ? totals.revenue / totals.orders : 0
  const aovLY = totals.compareOrders > 0 ? totals.compareRevenue / totals.compareOrders : 0

  const kpis: Array<{ key: string; label: string; value: string; note: string; pill?: { className: string; text: string } }> = [
    {
      key: 'orders', label: text('订单数', 'Orders'), value: formatInteger(totals.orders),
      note: `${reference} ${formatInteger(totals.compareOrders)}`, pill: trendPill(totals.orders, totals.compareOrders, text),
    },
    {
      key: 'aov', label: text('客单价', 'AOV'), value: formatAud(aov, 2),
      note: `${reference} ${formatAud(aovLY, 2)}`, pill: trendPill(aov, aovLY, text),
    },
  ]
  if (branchCounts) {
    kpis.push({
      key: 'branches', label: text('门店对比', 'Branches'),
      value: text(`${branchCounts.ahead} 家领先 · ${branchCounts.behind} 家落后`, `${branchCounts.ahead} ahead · ${branchCounts.behind} behind`),
      note: coversFullDay ? text('按全天同比', 'Full-day comparison') : text(`按截至 ${formatHourLabel(displayCutoff)} 同比`, `Compared to ${formatHourLabel(displayCutoff)}`),
    })
  }
  if (cutoff.live) {
    const remaining = fullDay.compareRevenue - fullDay.revenue
    kpis.push({
      key: 'catch-up', label: text('追平去年全天', 'To match LY day'),
      value: remaining > 0 ? text(`还差 ${formatAud(remaining)}`, `${formatAud(remaining)} to go`) : text(`已超出 ${formatAud(-remaining)}`, `${formatAud(-remaining)} over`),
      note: text(
        `去年 ${formatHourLabel(cutoff.cutoffHour)} 后还卖了 ${formatAud(fullDay.compareRevenue - getCumulativeTotals(series, cutoff.cutoffHour).compareRevenue)}`,
        `LY sold ${formatAud(fullDay.compareRevenue - getCumulativeTotals(series, cutoff.cutoffHour).compareRevenue)} after ${formatHourLabel(cutoff.cutoffHour)}`,
      ),
    })
  } else {
    let peak = series.firstHour
    for (let hour = series.firstHour; hour < series.endHour; hour += 1) if (series.revenue[hour]! > series.revenue[peak]!) peak = hour
    kpis.push({
      key: 'peak', label: text('峰值时段', 'Peak hour'), value: `${formatHourLabel(peak)}–${formatHourLabel(peak + 1)}`,
      note: `${formatAud(series.revenue[peak])} · ${text('去年', 'LY')} ${formatAud(series.compareRevenue[peak])}`,
      pill: trendPill(series.revenue[peak]!, series.compareRevenue[peak]!, text),
    })
  }

  // 逐小时累计条带：可点选的整点在前，之后尚未完整的整点显示为「未到」。
  const cells: Array<{ hour: number; active: boolean; label: string; tone: 'positive' | 'negative' | 'neutral'; lowBase: boolean; rate: number | null }> = []
  for (let hour = series.firstHour + 1; hour <= chartModel.endHour; hour += 1) {
    const active = options.includes(hour)
    const cellTotals = getCumulativeTotals(series, hour)
    const cellLowBase = isLowBase(cellTotals.compareRevenue, fullDay.compareRevenue)
    const trend = getRevenueTrend(cellTotals.revenue, cellTotals.compareRevenue)
    cells.push({
      hour,
      active,
      label: !active ? text('未到', 'Not yet') : cellLowBase ? signedAud(cellTotals.revenue - cellTotals.compareRevenue) : trend.text === 'new' ? text('新增', 'New') : trend.text,
      tone: trend.tone,
      lowBase: cellLowBase,
      rate: cellTotals.compareRevenue > 0 ? (cellTotals.revenue - cellTotals.compareRevenue) / cellTotals.compareRevenue : null,
    })
  }
  // 柱高按非小基数格子里的最大涨跌幅归一，小基数格子不参与，避免把其他柱子压扁。
  const maxRate = Math.max(0.001, ...cells.filter(cell => cell.active && !cell.lowBase && cell.rate !== null).map(cell => Math.abs(cell.rate!)))
  const halfBar = 15

  return (
    <section className={styles.panel} aria-labelledby="cumulative-revenue-title">
      {header}
      <div className={styles.body}>
        <div className={styles.summary}>
          <div>
            <div className={styles.asOf}><ClockCircleOutlined aria-hidden="true" />{asOf}</div>
            <div className={styles.headlineRow}>
              <strong className={styles.headline}>{formatAud(noCompleteHour ? fullDay.revenue : totals.revenue)}</strong>
              {!noCompleteHour && (
                <span className={`${styles.headlinePill} ${headlinePill.className}`} style={{ background: pillBackground }}>{headlinePill.text}</span>
              )}
            </div>
            <div className={styles.diffRow} aria-live="polite">
              {!noCompleteHour && <span>{differenceText}</span>}
              {userPickedCutoff && (
                <button type="button" className={styles.resetButton} onClick={onResetCutoff}>
                  {cutoff.live
                    ? text(`回到最新整点 ${formatHourLabel(getDisplayCutoffHour(series, cutoff.cutoffHour))}`, `Back to latest ${formatHourLabel(getDisplayCutoffHour(series, cutoff.cutoffHour))}`)
                    : text('回到全天', 'Back to full day')}
                </button>
              )}
            </div>
          </div>
          {!noCompleteHour && (
            <ul className={styles.kpiList}>
              {kpis.map(kpi => (
                <li key={kpi.key} className={styles.kpiItem}>
                  <span className={styles.kpiLabel}>{kpi.label}</span>
                  <span className={styles.kpiValue}><strong>{kpi.value}</strong><small>{kpi.note}</small></span>
                  {kpi.pill ? <span className={kpi.pill.className}>{kpi.pill.text}</span> : <span />}
                </li>
              ))}
            </ul>
          )}
        </div>
        <div className={styles.chartColumn}>
          <div className={styles.legend} aria-hidden="true">
            <span><i className={styles.legendLine} />{cutoff.live ? text('今天', 'Today') : text('所选日期', 'Selected day')}</span>
            <span><i className={styles.legendDash} />{text(`去年 ${compareLabel}`, `LY ${compareLabel}`)}</span>
            <span><i className={styles.legendGap} />{text('落后 / 领先去年', 'Behind / ahead of LY')}</span>
            {chartModel.liveTail && (
              <span><i className={styles.legendLiveDash} />{text(`进行中 · 实时 ${formatAud(chartModel.liveTail.value)}`, `In progress · live ${formatAud(chartModel.liveTail.value)}`)}</span>
            )}
          </div>
          <CumulativeRevenueChart
            model={chartModel}
            markerHour={displayCutoff}
            coversFullDay={coversFullDay}
            live={cutoff.live}
            ariaLabel={text(
              `${scopeLabel}截至 ${formatHourLabel(displayCutoff)} 的累计营业额：本期 ${formatAud(totals.revenue)}，去年 ${formatAud(totals.compareRevenue)}`,
              `${scopeLabel} cumulative revenue to ${formatHourLabel(displayCutoff)}: ${formatAud(totals.revenue)} vs ${formatAud(totals.compareRevenue)} last year`,
            )}
          />
        </div>
      </div>
      {cells.length > 0 && (
        <div className={styles.strip}>
          <div className={styles.stripHeader}>
            <span><h3>{text('逐小时累计涨跌', 'Cumulative change by hour')}</h3>{text('点选截止时刻，分店排行、时段表与周层级同步切换', 'Pick a cutoff; rankings, hourly and weekly tables follow')}</span>
            <span>{text('基数小 = 去年同时刻不足去年全天 5%，改显示金额差', 'Low base = under 5% of last year’s day; shows the dollar gap')}</span>
          </div>
          <div className={styles.stripScroll}>
            <div className={styles.stripCells} role="group" aria-label={text('选择截止时刻', 'Choose cutoff hour')}>
              {cells.map(cell => {
                const selected = cell.active && cell.hour === displayCutoff
                const barHeight = !cell.active || cell.lowBase || cell.rate === null ? 0 : Math.max(3, (Math.abs(cell.rate) / maxRate) * halfBar)
                const barColor = cell.tone === 'positive' ? '#16a34a' : cell.tone === 'negative' ? '#dc2626' : '#98a2b3'
                return (
                  <button
                    key={cell.hour}
                    type="button"
                    className={`${styles.stripCell} ${selected ? styles.stripCellSelected : ''}`}
                    disabled={!cell.active}
                    aria-pressed={selected}
                    aria-label={cell.active
                      ? text(`对比截至 ${formatHourLabel(cell.hour)}，${cell.label}`, `Compare to ${formatHourLabel(cell.hour)}, ${cell.label}`)
                      : text(`${formatHourLabel(cell.hour)} 未到`, `${formatHourLabel(cell.hour)} not reached`)}
                    onClick={() => onSelectCutoff(cell.hour)}
                  >
                    <span className={`${styles.stripValue} ${cell.active && !cell.lowBase ? pageStyles[cell.tone] : ''}`}>{cell.label}</span>
                    <span className={styles.stripBars} aria-hidden="true">
                      {cell.active && !cell.lowBase && <span className={styles.stripZero} />}
                      {barHeight > 0 && (
                        <span className={styles.stripBar} style={{
                          background: barColor,
                          height: barHeight,
                          // 零线在 halfBar 处：领先向上长，落后向下长。
                          top: cell.tone === 'positive' ? halfBar - barHeight : halfBar + 1,
                        }} />
                      )}
                      {cell.active && cell.lowBase && <span className={styles.stripLowBase}>{text('基数小', 'Low base')}</span>}
                    </span>
                    <span className={styles.stripHour}>
                      {formatHourLabel(cell.hour)}{!cutoff.live && cell.hour === series.endHour ? ` · ${text('全天', 'Day')}` : ''}
                    </span>
                  </button>
                )
              })}
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
