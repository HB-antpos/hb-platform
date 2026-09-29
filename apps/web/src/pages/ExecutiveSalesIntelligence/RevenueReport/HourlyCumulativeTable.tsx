import { useMemo } from 'react'
import { useReportText } from '../ReportWorkbench/ReportControls'
import {
  FULL_DAY_CUTOFF_HOUR,
  buildHourlyDetailRows,
  formatHourLabel,
  getCumulativeTotals,
  isLowBase,
  type CutoffResolution,
  type HourlySeries,
} from './hourlyCumulative'
import { formatAud, getRevenueTrend } from './logic'
import pageStyles from '../styles.module.css'
import styles from './cumulative.module.css'

export type HourlyTableMode = 'cumulative' | 'perHour'

export interface HourlyCumulativeTableProps {
  series: HourlySeries
  cutoff: CutoffResolution
  effectiveCutoffHour: number
  mode: HourlyTableMode
  liveClock: string | null
  onSelectCutoff: (hour: number) => void
}

/**
 * 单日时段表：累计口径从开门累加到每个整点，逐小时口径与原表一致。
 * 进行中的小时不算同比，未到的小时只显示去年值，不再出现 $0 / −100%。
 */
export default function HourlyCumulativeTable({ series, cutoff, effectiveCutoffHour, mode, liveClock, onSelectCutoff }: HourlyCumulativeTableProps) {
  const text = useReportText()
  const cumulative = mode === 'cumulative'
  const rows = useMemo(() => buildHourlyDetailRows(series, {
    cutoffHour: cutoff.cutoffHour,
    live: cutoff.live,
    cumulative,
    highlightCutoffHour: Math.min(effectiveCutoffHour, series.endHour ?? FULL_DAY_CUTOFF_HOUR),
  }), [cumulative, cutoff.cutoffHour, cutoff.live, effectiveCutoffHour, series])
  const fullDayCompare = getCumulativeTotals(series, FULL_DAY_CUTOFF_HOUR).compareRevenue
  const maxCompleteHour = Math.max(0, ...rows.filter(row => row.status === 'complete').map(row => row.revenue))
  const liveLabel = liveClock ?? formatHourLabel(cutoff.cutoffHour)

  return (
    <div className={pageStyles.tableScroll}>
      <table className={`${pageStyles.dataTable} ${pageStyles.hourlyTable}`}>
        <thead><tr>
          <th scope="col">{cumulative ? text('截至', 'To') : text('时段', 'Hour')}</th>
          <th scope="col">{cumulative ? text('本期累计', 'Current total') : text('本期', 'Current')}</th>
          <th scope="col">{cumulative ? text('去年累计', 'LY total') : text('同期', 'Previous')}</th>
          <th scope="col">{text('同比', 'YoY')}</th>
          <th scope="col">{cumulative ? text('达去年全天', 'Of LY day') : text('销售密度', 'Density')}</th>
        </tr></thead>
        <tbody>{rows.map(row => {
          const label = formatHourLabel(cumulative ? row.boundaryHour : row.hour)
          const lowBase = cumulative && isLowBase(row.compareRevenue, fullDayCompare)
          const trend = getRevenueTrend(row.revenue, row.compareRevenue)
          const percent = row.status === 'upcoming'
            ? null
            : cumulative
              ? fullDayCompare > 0 ? Math.round((row.revenue / fullDayCompare) * 100) : null
              : maxCompleteHour > 0 ? Math.round((row.revenue / maxCompleteHour) * 100) : 0
          const rowClass = [
            row.isCutoffRow ? styles.cutoffRow : '',
            row.status === 'upcoming' ? styles.upcomingRow : '',
            !cumulative && row.status === 'complete' && maxCompleteHour > 0 && row.revenue >= maxCompleteHour * 0.8 ? pageStyles.peakRow : '',
          ].filter(Boolean).join(' ') || undefined
          return (
            <tr key={row.hour} className={rowClass}>
              <th scope="row">
                {cumulative && row.status === 'complete' ? (
                  // 累计行可直接点选为截止时刻，与上方条带联动。
                  <button type="button" className={styles.hourSelect} aria-pressed={row.isCutoffRow}
                    aria-label={text(`对比截至 ${label}`, `Compare to ${label}`)} onClick={() => onSelectCutoff(row.boundaryHour)}>
                    {label}{row.isCutoffRow && <span className={styles.cutoffTag}>{text('截止', 'Cutoff')}</span>}
                  </button>
                ) : (
                  <span className={styles.hourLabel}>{label}</span>
                )}
              </th>
              <td>
                {row.status === 'upcoming' ? (
                  <span className={styles.cellWithTag}><span className={pageStyles.mutedValue}>—</span><span className={styles.mutedTag}>{text('未到', 'Not yet')}</span></span>
                ) : row.status === 'live' ? (
                  <span className={styles.cellWithTag}><span className={styles.liveValue}>{formatAud(row.revenue)}</span><span className={styles.liveTag}>{text(`实时 ${liveLabel}`, `Live ${liveLabel}`)}</span></span>
                ) : (
                  <span className={pageStyles.numeric}>{formatAud(row.revenue)}</span>
                )}
              </td>
              <td className={pageStyles.mutedValue}>{formatAud(row.compareRevenue)}</td>
              <td>
                {row.status === 'complete' ? (
                  lowBase
                    ? <span className={`${pageStyles.trend} ${pageStyles.neutral}`} title={text('去年同时刻基数太小，显示金额差', 'Low base; dollar gap shown')}>
                      {`${row.revenue - row.compareRevenue >= 0 ? '+' : '−'}${formatAud(Math.abs(row.revenue - row.compareRevenue))}`}
                    </span>
                    : <span className={`${pageStyles.trend} ${pageStyles[trend.tone]}`}>{trend.text === 'new' ? text('新增', 'New') : trend.text}</span>
                ) : (
                  <span className={pageStyles.mutedValue}>
                    {row.status === 'live' ? text(`${formatHourLabel(row.boundaryHour)} 后`, `After ${formatHourLabel(row.boundaryHour)}`) : '—'}
                  </span>
                )}
              </td>
              <td>
                {percent === null ? null : (
                  <div className={pageStyles.progressCell}><progress value={Math.min(100, percent)} max={100}>{percent}%</progress><span>{percent}%</span></div>
                )}
              </td>
            </tr>
          )
        })}</tbody>
      </table>
    </div>
  )
}
