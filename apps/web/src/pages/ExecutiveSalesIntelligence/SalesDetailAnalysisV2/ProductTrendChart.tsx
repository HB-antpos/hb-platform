import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'

import { monthDay, niceScale, previousPurchase, weekdayOf, type MonthPoint, type TrendPoint } from './drawerLogic'
import styles from './styles.module.css'

export type TrendGranularity = 'day' | 'month'

interface ProductTrendChartProps {
  points: TrendPoint[]
  months: MonthPoint[]
  granularity: TrendGranularity
  salesStart: string
  salesEnd: string
  /** 窗口内没有任何销量：画平线并写明，进货柱照常显示。 */
  emptySales: boolean
  text: (zh: string, en: string) => string
}

// 与「澳洲本地商品分析」一致：销量橙色、进货蓝色（总仓）与绿色（供应商）。
const COLOR = { sales: '#e8710a', warehouse: '#5b8fd9', supplier: '#3aa57a', grid: '#e6ecf3', axis: '#8aa0b6', band: '#eaf3fe', bandLine: '#a9cdf2', hover: '#9fb3c8' }
const LEFT = 40
const RIGHT = 8
const SALES_TOP = 18
const SALES_H = 128
const CAPTION_H = 26
const BUY_H = 56
const AXIS_H = 26
const integer = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })
const WEEKDAYS_ZH = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
const WEEKDAYS_EN = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

function useWidth() {
  const ref = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(800)
  useEffect(() => {
    const element = ref.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.max(320, Math.floor(entry.contentRect.width))))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  return [ref, width] as const
}

/** 销售曲线 + 进货柱：上下两个图共用同一条时间轴；日 / 月两种粒度，悬停与键盘左右键查看某一天（某一月）。 */
export default function ProductTrendChart({ points, months, granularity, salesStart, salesEnd, emptySales, text }: ProductTrendChartProps) {
  const [containerRef, width] = useWidth()
  const gradientId = `trend-${useId().replace(/[^a-zA-Z0-9]/g, '')}`
  const hatchId = `hatch-${gradientId}`
  const [hover, setHover] = useState<number | null>(null)
  const isDay = granularity === 'day'
  const count = isDay ? points.length : months.length
  // 切换粒度或数据变化后，悬停位置不再有效。
  useEffect(() => { setHover(null) }, [granularity, points, months])

  const plotWidth = width - LEFT - RIGHT
  const step = count > 0 ? plotWidth / count : plotWidth
  const x = useCallback((index: number) => LEFT + step * (index + 0.5), [step])
  const salesValues = isDay ? points.map(point => point.sales) : months.map(month => month.sales)
  const buyValues = isDay ? points.map(point => point.warehouse + point.supplier) : months.map(month => month.warehouse + month.supplier)
  const salesScale = useMemo(() => niceScale(Math.max(0, ...salesValues), isDay ? 4 : 3), [salesValues, isDay])
  const buyScale = useMemo(() => niceScale(Math.max(0, ...buyValues), isDay ? 3 : 2), [buyValues, isDay])
  const salesY = (value: number) => SALES_TOP + SALES_H - (value / salesScale.max) * SALES_H
  const buyTop = SALES_TOP + SALES_H + CAPTION_H
  const buyY = (value: number) => buyTop + BUY_H - (value / buyScale.max) * BUY_H
  const height = buyTop + BUY_H + AXIS_H
  const barWidth = isDay ? Math.max(2, step * 0.62) : Math.min(64, step * 0.5)
  const weekdayName = (date: string) => text(WEEKDAYS_ZH[weekdayOf(date)], WEEKDAYS_EN[weekdayOf(date)])

  const bandStart = isDay ? points.findIndex(point => point.date >= salesStart) : -1
  const bandEnd = isDay ? points.reduce((last, point, index) => (point.date <= salesEnd ? index : last), -1) : -1
  const bandX = bandStart >= 0 ? x(bandStart) - step / 2 : 0
  const bandW = bandStart >= 0 && bandEnd >= bandStart ? x(bandEnd) + step / 2 - bandX : 0

  const indexFromPointer = (event: MouseEvent<SVGRectElement>) => {
    const box = event.currentTarget.getBoundingClientRect()
    return Math.min(count - 1, Math.max(0, Math.floor((event.clientX - box.left - LEFT) / step)))
  }
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (count === 0) return
    const current = hover ?? (isDay ? Math.max(0, bandEnd) : count - 1)
    if (event.key === 'ArrowLeft') setHover(Math.max(0, current - 1))
    else if (event.key === 'ArrowRight') setHover(Math.min(count - 1, current + 1))
    else if (event.key === 'Home') setHover(0)
    else if (event.key === 'End') setHover(count - 1)
    else if (event.key === 'Escape') setHover(null)
    else return
    event.preventDefault()
  }

  const salesTotal = salesValues.reduce((sum, value) => sum + value, 0)
  const buyTotal = buyValues.reduce((sum, value) => sum + value, 0)
  const peakIndex = salesValues.indexOf(Math.max(0, ...salesValues))

  // 日视图横轴：从最后一天往前按固定间隔标日期，保证最后一天一定有标签；间隔随每天宽度放宽。
  const tickEvery = step >= 11 ? 7 : step >= 5 ? 14 : step >= 2.5 ? 30 : 60
  const tickIndexes = isDay ? Array.from({ length: Math.floor((count - 1) / tickEvery) + 1 }, (_, m) => count - 1 - m * tickEvery) : []
  const dayLabel = (date: string) => (count > 200 ? date.slice(2, 10) : monthDay(date))

  const tooltip = (() => {
    if (hover === null || count === 0) return null
    if (isDay) {
      const point = points[hover]
      const buy = point.warehouse + point.supplier
      const previous = buy === 0 ? previousPurchase(points, hover) : null
      return { x: x(hover), lines: [
        `${point.date} ${weekdayName(point.date)}${point.date >= salesStart && point.date <= salesEnd ? ` · ${text('本期销售期', 'Sales period')}` : ''}`,
        `${text('销量', 'Sold')} ${integer.format(point.sales)} ${text('件', 'pcs')}`,
        `${text('进货', 'Purchased')} ${integer.format(buy)} ${text('件', 'pcs')}${buy > 0 ? ` (${text('总仓', 'Warehouse')} ${integer.format(point.warehouse)} · ${text('供应商', 'Supplier')} ${integer.format(point.supplier)})` : ''}`,
        previous ? `${text('前一次进货', 'Previous purchase')} ${monthDay(previous.date)} · ${integer.format(previous.quantity)} ${text('件', 'pcs')}` : '',
      ].filter(Boolean) }
    }
    const month = months[hover]
    const buy = month.warehouse + month.supplier
    return { x: x(hover), lines: [
      `${month.key}${month.partial ? ` · ${text(`仅 ${month.days} 日`, `${month.days} day(s) only`)}` : ''}${month.hasSalesPeriod ? ` · ${text('含本期', 'Incl. sales period')}` : ''}`,
      `${text('销量', 'Sold')} ${integer.format(month.sales)} ${text('件', 'pcs')}`,
      `${text('进货', 'Purchased')} ${integer.format(buy)} ${text('件', 'pcs')}${buy > 0 ? ` (${text('总仓', 'Warehouse')} ${integer.format(month.warehouse)} · ${text('供应商', 'Supplier')} ${integer.format(month.supplier)})` : ''}`,
    ] }
  })()

  const label = text(
    `销售曲线：窗口内销量合计 ${integer.format(salesTotal)} 件，进货合计 ${integer.format(buyTotal)} 件。可用左右方向键逐${isDay ? '日' : '月'}查看。`,
    `Sales curve: ${integer.format(salesTotal)} pcs sold and ${integer.format(buyTotal)} pcs purchased in the window. Use the arrow keys to move ${isDay ? 'day' : 'month'} by ${isDay ? 'day' : 'month'}.`,
  )

  return <div ref={containerRef} className={styles.trendCanvas} tabIndex={0} role="group" aria-label={label} onKeyDown={onKeyDown} onBlur={() => setHover(null)}>
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} className={styles.trendSvg} aria-hidden="true">
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={COLOR.sales} stopOpacity=".22" />
          <stop offset="1" stopColor={COLOR.sales} stopOpacity="0" />
        </linearGradient>
        <pattern id={hatchId} width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
          <rect width="6" height="6" fill="rgba(232,113,10,.35)" />
          <line x1="0" y1="0" x2="0" y2="6" stroke="#fff" strokeWidth="2.4" />
        </pattern>
      </defs>
      {isDay && bandW > 0 && <>
        <rect x={bandX} y={SALES_TOP - 12} width={bandW} height={SALES_H + 12} fill={COLOR.band} stroke={COLOR.bandLine} strokeDasharray="3 3" />
        <rect x={bandX} y={buyTop - 8} width={bandW} height={BUY_H + 8} fill={COLOR.band} stroke={COLOR.bandLine} strokeDasharray="3 3" />
        <text x={bandX + bandW / 2} y={SALES_TOP - 2} textAnchor="middle" fontSize="10" fontWeight="600" fill="#1c5ea6">{text('本期', 'Sales')}</text>
      </>}
      {salesScale.ticks.map(tick => <g key={`s${tick}`}>
        <line x1={LEFT} x2={width - RIGHT} y1={salesY(tick)} y2={salesY(tick)} stroke={COLOR.grid} />
        <text x={LEFT - 6} y={salesY(tick) + 3} textAnchor="end" fontSize="10" fill={COLOR.axis}>{integer.format(tick)}</text>
      </g>)}
      {isDay && points.map((point, index) => point.date.endsWith('-01') && index > 0 && <g key={`m${point.date}`}>
        <line x1={x(index) - step / 2} x2={x(index) - step / 2} y1={SALES_TOP} y2={SALES_TOP + SALES_H} stroke="#c9d5e2" strokeDasharray="2 3" />
        {x(index) + 24 < width - RIGHT - 16 && <text x={x(index) - step / 2 + 4} y={SALES_TOP + 10} fontSize="10" fill={COLOR.axis}>{text(`${Number(point.date.slice(5, 7))}月`, `M${Number(point.date.slice(5, 7))}`)}</text>}
      </g>)}
      {isDay && count > 0 && <>
        <path d={`M${x(0).toFixed(1)},${salesY(points[0].sales).toFixed(1)} ${points.map((point, index) => `L${x(index).toFixed(1)},${salesY(point.sales).toFixed(1)}`).join(' ')} L${x(count - 1).toFixed(1)},${salesY(0)} L${x(0).toFixed(1)},${salesY(0)} Z`} fill={`url(#${gradientId})`} />
        <polyline points={points.map((point, index) => `${x(index).toFixed(1)},${salesY(point.sales).toFixed(1)}`).join(' ')} fill="none" stroke={COLOR.sales} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
        {points.map((point, index) => point.date >= salesStart && point.date <= salesEnd && <circle key={`c${point.date}`} cx={x(index)} cy={salesY(point.sales)} r="2.6" fill="#fff" stroke={COLOR.sales} strokeWidth="1.6" />)}
        {!emptySales && hover === null && salesValues[peakIndex] > 0 && <text x={x(peakIndex) + (peakIndex > count / 2 ? -6 : 6)} y={salesY(salesValues[peakIndex]) - 6} textAnchor={peakIndex > count / 2 ? 'end' : 'start'} fontSize="10" fontWeight="600" fill={COLOR.sales}>
          {text('峰值', 'Peak')} {monthDay(points[peakIndex].date)} · {integer.format(salesValues[peakIndex])}
        </text>}
      </>}
      {!isDay && months.map((month, index) => <g key={`ms${month.key}`}>
        <rect x={x(index) - barWidth / 2} y={salesY(month.sales)} width={barWidth} height={Math.max(0, salesY(0) - salesY(month.sales))} rx="3"
          fill={month.partial ? `url(#${hatchId})` : COLOR.sales} stroke={month.partial ? COLOR.sales : undefined} strokeDasharray={month.partial ? '3 2' : undefined} />
        <text x={x(index)} y={salesY(month.sales) - 5} textAnchor="middle" fontSize="11" fontWeight="600" fill="#143452">{integer.format(month.sales)}</text>
      </g>)}
      {emptySales && <text x={LEFT + plotWidth / 2} y={SALES_TOP + SALES_H / 2 - 8} textAnchor="middle" fontSize="12" fill="#667085">{text('窗口内没有销量', 'No sales in this window')}</text>}
      {emptySales && isDay && <line x1={LEFT} x2={width - RIGHT} y1={salesY(0)} y2={salesY(0)} stroke={COLOR.sales} strokeWidth="2" />}

      <text x={LEFT} y={SALES_TOP + SALES_H + 18} fontSize="10" fill="#5c7088">{text('进货量（件）', 'Purchased (pcs)')}</text>
      {buyScale.ticks.map(tick => <g key={`b${tick}`}>
        <line x1={LEFT} x2={width - RIGHT} y1={buyY(tick)} y2={buyY(tick)} stroke={COLOR.grid} />
        <text x={LEFT - 6} y={buyY(tick) + 3} textAnchor="end" fontSize="10" fill={COLOR.axis}>{integer.format(tick)}</text>
      </g>)}
      {(isDay ? points.map(point => ({ key: point.date, warehouse: point.warehouse, supplier: point.supplier })) : months.map(month => ({ key: month.key, warehouse: month.warehouse, supplier: month.supplier })))
        .map((item, index) => {
          const total = item.warehouse + item.supplier
          if (total === 0) return !isDay ? <text key={`bz${item.key}`} x={x(index)} y={buyY(0) - 5} textAnchor="middle" fontSize="11" fill="#98a2b3">—</text> : null
          const warehouseH = (item.warehouse / buyScale.max) * BUY_H
          const supplierH = (item.supplier / buyScale.max) * BUY_H
          const radius = isDay ? 1.5 : 3
          return <g key={`bb${item.key}`}>
            {item.warehouse > 0 && <rect x={x(index) - barWidth / 2} y={buyY(0) - warehouseH} width={barWidth} height={warehouseH} rx={radius} fill={COLOR.warehouse} />}
            {item.supplier > 0 && <rect x={x(index) - barWidth / 2} y={buyY(0) - warehouseH - supplierH} width={barWidth} height={supplierH} rx={radius} fill={COLOR.supplier} />}
            {(!isDay || step >= 9) && <text x={x(index)} y={buyY(0) - warehouseH - supplierH - 3} textAnchor="middle" fontSize={isDay ? 10 : 11} fontWeight="600" fill="#355576">{integer.format(total)}</text>}
          </g>
        })}
      {count > 0 && buyValues.every(value => value === 0) && <text x={LEFT + plotWidth / 2} y={buyTop + BUY_H / 2 - 4} textAnchor="middle" fontSize="12" fill="#667085">{text('窗口内没有进货记录', 'No purchases in this window')}</text>}
      {isDay && tickIndexes.map(index => <text key={`t${index}`} x={x(index)} y={buyTop + BUY_H + 15} textAnchor="middle" fontSize="10" fill={index === count - 1 ? '#1c5ea6' : COLOR.axis} fontWeight={index === count - 1 ? 600 : 400}>{dayLabel(points[index].date)}</text>)}
      {!isDay && months.map((month, index) => <g key={`ml${month.key}`}>
        <text x={x(index)} y={buyTop + BUY_H + 15} textAnchor="middle" fontSize="12" fontWeight="600" fill="#143452">{text(`${month.month}月`, `${month.key}`)}</text>
        <text x={x(index)} y={buyTop + BUY_H + 28} textAnchor="middle" fontSize="10" fill={month.hasSalesPeriod ? '#1c5ea6' : COLOR.axis}>
          {[month.hasSalesPeriod ? text('含本期', 'Incl. sales') : '', month.partial ? text(`仅 ${month.days} 日`, `${month.days}d only`) : ''].filter(Boolean).join(' · ')}
        </text>
      </g>)}
      {hover !== null && <line x1={x(hover)} x2={x(hover)} y1={SALES_TOP - 12} y2={buyTop + BUY_H} stroke={COLOR.hover} strokeDasharray="3 3" />}
      {hover !== null && isDay && <circle cx={x(hover)} cy={salesY(points[hover].sales)} r="4.2" fill={COLOR.sales} stroke="#fff" strokeWidth="2" />}
      <rect x={LEFT} y={SALES_TOP - 12} width={plotWidth} height={buyTop + BUY_H - SALES_TOP + 12} fill="transparent"
        onMouseMove={event => setHover(indexFromPointer(event))} onMouseLeave={() => setHover(null)} />
    </svg>
    {tooltip && <div className={styles.trendTip} style={tooltip.x > width * 0.55 ? { right: width - tooltip.x + 12, top: 30 } : { left: tooltip.x + 12, top: 30 }} role="status">
      {tooltip.lines.map((line, index) => <span key={index}>{line}</span>)}
    </div>}
  </div>
}
