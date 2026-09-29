import { useEffect, useRef, useState } from 'react'
import { useReportText } from '../ReportWorkbench/ReportControls'
import { formatHourLabel, isLowBase, type CumulativeChartModel, type CumulativePoint } from './hourlyCumulative'
import { formatAud, getRevenueTrend } from './logic'
import styles from './cumulative.module.css'

const HEIGHT = 260
const MARGIN = { top: 20, bottom: 28, left: 56 }
const WIDE_RIGHT_MARGIN = 128
const NARROW_RIGHT_MARGIN = 16
const BOX_WIDTH = 168
const BOX_HEIGHT = 82

const COLORS = {
  current: '#0f3d73',
  compare: '#8a97a8',
  grid: '#e9eef3',
  baseline: '#c9d2dc',
  marker: '#1668dc',
  band: '#f5f7fa',
  behind: 'rgb(220 38 38 / 20%)',
  ahead: 'rgb(22 163 74 / 24%)',
}

/** 纵轴取好读的刻度：不超过 5 格（1/2/2.5/5 × 10ⁿ），顶部至少留 8% 给终点标签，避免大片空白。 */
function getNiceScale(maxValue: number) {
  const target = Math.max(maxValue, 1) * 1.08
  const magnitude = 10 ** Math.floor(Math.log10(target / 5))
  for (const factor of [1, 2, 2.5, 5, 10]) {
    const step = factor * magnitude
    if (target / step <= 5) return { max: Math.ceil(target / step) * step, step }
  }
  return { max: Math.ceil(target / (10 * magnitude)) * 10 * magnitude, step: 10 * magnitude }
}

function formatAxisMoney(value: number) {
  if (value === 0) return '$0'
  return value >= 1_000 ? `$${Math.round(value / 100) / 10}k` : `$${Math.round(value)}`
}

function useElementWidth<T extends HTMLElement>() {
  const ref = useRef<T | null>(null)
  const [width, setWidth] = useState(0)
  useEffect(() => {
    const element = ref.current
    if (!element) return
    const update = () => setWidth(Math.round(element.getBoundingClientRect().width))
    update()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  return { ref, width }
}

export interface CumulativeRevenueChartProps {
  model: CumulativeChartModel
  /** 用户选中的截止整点：曲线始终画到最近的完整整点，选中整点只移动标记，之后的走势不丢。 */
  markerHour: number
  coversFullDay: boolean
  live: boolean
  ariaLabel: string
}

export default function CumulativeRevenueChart({ model, markerHour, coversFullDay, live, ariaLabel }: CumulativeRevenueChartProps) {
  const text = useReportText()
  const { ref, width } = useElementWidth<HTMLDivElement>()
  const rightMargin = width >= 560 ? WIDE_RIGHT_MARGIN : NARROW_RIGHT_MARGIN
  const plotLeft = MARGIN.left
  const plotRight = Math.max(plotLeft + 1, width - rightMargin)
  const plotTop = MARGIN.top
  const plotBottom = HEIGHT - MARGIN.bottom
  const span = Math.max(1, model.endHour - model.startHour)
  const scale = getNiceScale(model.maxValue)
  const x = (hour: number) => plotLeft + ((hour - model.startHour) / span) * (plotRight - plotLeft)
  const y = (value: number) => plotBottom - (value / scale.max) * (plotBottom - plotTop)
  const toPath = (points: readonly CumulativePoint[]) => points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${x(point.hour).toFixed(1)} ${y(point.value).toFixed(1)}`)
    .join(' ')

  const gridValues: number[] = []
  for (let value = 0; value <= scale.max; value += scale.step) gridValues.push(value)
  // 每小时不足 44px 时隔几个整点标一次，避免时间刻度挤在一起。
  const labelStep = Math.max(1, Math.ceil(44 / Math.max(1, (plotRight - plotLeft) / span)))
  const hourLabels: number[] = []
  for (let hour = model.startHour; hour <= model.endHour; hour += labelStep) hourLabels.push(hour)

  const lastCurrent = model.currentPoints[model.currentPoints.length - 1]
  const compareEnd = model.comparePoints[model.comparePoints.length - 1]
  const markerCurrent = model.currentPoints.find(point => point.hour === markerHour)
  const markerCompare = model.comparePoints.find(point => point.hour === markerHour)
  const bandStart = live && lastCurrent ? x(lastCurrent.hour) : null

  // 标记框避开曲线：累计值单调递增，标记点右下方与左上方总有一块是空的。
  let box: { left: number; top: number } | null = null
  if (markerCurrent && markerCompare) {
    const markerX = x(markerHour)
    const topY = plotTop + 6
    const bottomY = plotBottom - BOX_HEIGHT - 6
    const roomRight = markerX + 12 + BOX_WIDTH <= plotRight
    const roomLeft = markerX - 12 - BOX_WIDTH >= plotLeft
    const lowerY = y(Math.min(markerCurrent.value, markerCompare.value))
    const upperY = y(Math.max(markerCurrent.value, markerCompare.value))
    if (roomRight && lowerY < bottomY - 8) box = { left: markerX + 12, top: bottomY }
    else if (roomLeft && topY + BOX_HEIGHT < upperY - 8) box = { left: markerX - 12 - BOX_WIDTH, top: topY }
    else if (roomRight) box = { left: markerX + 12, top: topY }
    else box = { left: Math.max(plotLeft, markerX - 12 - BOX_WIDTH), top: bottomY }
  }
  const markerDiff = markerCurrent && markerCompare ? markerCurrent.value - markerCompare.value : 0
  const markerLowBase = markerCompare && compareEnd ? isLowBase(markerCompare.value, compareEnd.value) : false
  const markerTrend = getRevenueTrend(markerCurrent?.value, markerCompare?.value)
  const signedDiff = `${markerDiff > 0 ? '+' : markerDiff < 0 ? '−' : '±'}${formatAud(Math.abs(markerDiff))}`

  // 终点标签：去年全天，历史日期再加当日合计；两者太近时上下错开。
  const endLabels: Array<{ key: string; text: string; color: string; y: number }> = []
  if (compareEnd && rightMargin === WIDE_RIGHT_MARGIN) {
    endLabels.push({ key: 'compare', text: `${text('去年全天', 'LY day')} ${formatAud(compareEnd.value)}`, color: '#596d85', y: y(compareEnd.value) })
    if (!live && lastCurrent && lastCurrent.hour === model.endHour) {
      endLabels.push({ key: 'current', text: `${text('当日', 'Day')} ${formatAud(lastCurrent.value)}`, color: COLORS.current, y: y(lastCurrent.value) })
    }
    if (endLabels.length === 2 && Math.abs(endLabels[0]!.y - endLabels[1]!.y) < 16) {
      const middle = (endLabels[0]!.y + endLabels[1]!.y) / 2
      const higher = endLabels[0]!.y <= endLabels[1]!.y ? 0 : 1
      endLabels[higher]!.y = middle - 8
      endLabels[1 - higher]!.y = middle + 8
    }
  }

  const aheadPath = model.gapRegions.filter(region => region.tone === 'ahead')
  const behindPath = model.gapRegions.filter(region => region.tone === 'behind')
  const regionPath = (regions: typeof model.gapRegions) => regions.map(region => {
    const forward = region.points.map((point, index) => `${index === 0 ? 'M' : 'L'}${x(point.hour).toFixed(1)} ${y(point.current).toFixed(1)}`)
    const back = [...region.points].reverse().map(point => `L${x(point.hour).toFixed(1)} ${y(point.compare).toFixed(1)}`)
    return `${forward.join(' ')} ${back.join(' ')} Z`
  }).join(' ')

  return (
    <div ref={ref} className={styles.chart} role="img" aria-label={ariaLabel}>
      {width > 0 && (
        <>
          <svg width={width} height={HEIGHT} aria-hidden="true">
            {bandStart !== null && (
              <rect x={bandStart} y={plotTop} width={Math.max(0, x(model.endHour) - bandStart)} height={plotBottom - plotTop} fill={COLORS.band} />
            )}
            {gridValues.map(value => (
              <line key={value} x1={plotLeft} x2={plotRight} y1={y(value)} y2={y(value)}
                stroke={value === 0 ? COLORS.baseline : COLORS.grid} strokeWidth={1} />
            ))}
            <path d={regionPath(behindPath)} fill={COLORS.behind} />
            <path d={regionPath(aheadPath)} fill={COLORS.ahead} />
            <path d={toPath(model.comparePoints)} fill="none" stroke={COLORS.compare} strokeWidth={2} strokeDasharray="6 4" strokeLinejoin="round" />
            <path d={toPath(model.currentPoints)} fill="none" stroke={COLORS.current} strokeWidth={2.5} strokeLinejoin="round" strokeLinecap="round" />
            {model.liveTail && lastCurrent && (
              <path d={`M${x(lastCurrent.hour)} ${y(lastCurrent.value)} L${x(model.liveTail.hour)} ${y(model.liveTail.value)}`}
                fill="none" stroke={COLORS.current} strokeWidth={2} strokeDasharray="3 3" />
            )}
            {markerCurrent && markerCompare && (
              <>
                <line x1={x(markerHour)} x2={x(markerHour)} y1={plotTop} y2={plotBottom} stroke={COLORS.marker} strokeWidth={1} strokeDasharray="3 3" />
                <circle cx={x(markerHour)} cy={y(markerCompare.value)} r={4} fill="#fff" stroke={COLORS.compare} strokeWidth={2} />
                <circle cx={x(markerHour)} cy={y(markerCurrent.value)} r={4.5} fill={COLORS.current} stroke="#fff" strokeWidth={2} />
              </>
            )}
          </svg>
          {gridValues.map(value => (
            <span key={value} className={styles.chartTick} style={{ left: 0, width: plotLeft - 10, textAlign: 'right', top: y(value) - 7 }}>
              {formatAxisMoney(value)}
            </span>
          ))}
          {hourLabels.map(hour => (
            <span key={hour} className={styles.chartTick} style={{ left: x(hour) - 20, width: 40, textAlign: 'center', top: plotBottom + 7 }}>
              {formatHourLabel(hour)}
            </span>
          ))}
          {endLabels.map(label => (
            <span key={label.key} className={styles.chartEndLabel} style={{ left: x(model.endHour) + 8, top: label.y - 7, color: label.color }}>
              {label.text}
            </span>
          ))}
          {bandStart !== null && x(model.endHour) - bandStart > 40 && (
            // 放在未到区域右上角：右下角是标记框的首选位置，会把它盖住。
            <span className={styles.chartTick} style={{ left: x(model.endHour) - 44, top: plotTop + 4 }}>{text('未到', 'Not yet')}</span>
          )}
          {box && markerCurrent && markerCompare && (
            <div className={styles.markerBox} style={{ left: box.left, top: box.top }}>
              <strong>{coversFullDay ? text('全天', 'Full day') : `${text('截至', 'To')} ${formatHourLabel(markerHour)}`}</strong>
              <span className={styles.markerLine}>
                <span><i className={styles.legendLine} aria-hidden="true" />{live ? text('今天', 'Today') : text('当日', 'Day')}</span>
                <strong>{formatAud(markerCurrent.value)}</strong>
              </span>
              <span className={styles.markerLine}>
                <span><i className={styles.legendDash} aria-hidden="true" />{text('去年', 'LY')}</span>
                <span>{formatAud(markerCompare.value)}</span>
              </span>
              <strong
                style={{ color: markerLowBase ? '#596d85' : markerTrend.tone === 'positive' ? '#137333' : markerTrend.tone === 'negative' ? '#b42318' : '#596d85' }}>
                {markerLowBase
                  ? `${signedDiff} · ${text('基数小', 'Low base')}`
                  : `${signedDiff} · ${markerTrend.text === 'new' ? text('新增', 'New') : markerTrend.text}`}
              </strong>
            </div>
          )}
        </>
      )}
    </div>
  )
}
