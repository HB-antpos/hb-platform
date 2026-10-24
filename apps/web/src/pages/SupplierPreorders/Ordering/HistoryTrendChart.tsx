// 展开行大图（纯 SVG）：上半去年日销量（橙色），下半进货柱（蓝 = 总仓配货，绿 = 供应商直送），共用一条时间轴。
// 节日关键日期画紫色虚线并加标签：相邻日期合并成一个标签，仍会相撞的按像素碰撞错层摆放；折扣期铺粉色底；标出峰值。
// 悬停显示当天销量 / 折扣销量 / 进货。做法参考销售明细分析的 ProductTrendChart，但不跨页引用。
import type { TFunction } from 'i18next'
import { useEffect, useMemo, useRef, useState, type MouseEvent } from 'react'

import type { SupplierPreorderDateRange, SupplierPreorderKeyDate } from '../../../types/supplierPreorder'

import {
  dateAtOffset,
  estimateTextWidth,
  findPeak,
  formatDayRange,
  formatMonthDay,
  groupKeyDateMarks,
  keyDateMarks,
  layoutLabelRows,
  monthStartTicks,
  niceScale,
  rangesToIndexSpans,
  seriesX,
} from './orderingHistory.logic'
import { HISTORY_COLORS, keyDateName } from './orderingHistoryCells'
import { formatInteger } from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

const LEFT = 36
const RIGHT = 12
const LABEL_TOP = 4
const LABEL_ROW_H = 20
const LABEL_FONT = 10
const SALES_H = 180
const CAPTION_H = 22
const BUY_H = 56
const AXIS_H = 22
const GRID = '#eef0f4'
const AXIS = '#98a2b3'

interface HistoryTrendChartProps {
  t: TFunction
  windowFrom?: string | null
  length: number
  sales: number[]
  discount: number[]
  warehouse: number[]
  supplier: number[]
  keyDates: readonly SupplierPreorderKeyDate[]
  discountRanges: readonly SupplierPreorderDateRange[]
}

/** 跟随容器宽度（最小 360px） */
function useContainerWidth() {
  const ref = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(720)
  useEffect(() => {
    const element = ref.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.max(360, Math.floor(entry.contentRect.width))))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  return [ref, width] as const
}

export default function HistoryTrendChart({ t, windowFrom, length, sales, discount, warehouse, supplier, keyDates, discountRanges }: HistoryTrendChartProps) {
  const [containerRef, width] = useContainerWidth()
  const [hover, setHover] = useState<number | null>(null)
  // 数据变化后旧的悬停位置不再有效
  useEffect(() => {
    setHover(null)
  }, [sales])

  const plotWidth = width - LEFT - RIGHT
  const x = (index: number) => seriesX(index, length, LEFT, plotWidth)
  const dayWidth = length > 1 ? plotWidth / (length - 1) : plotWidth

  // 节日标签：先合并相邻日期，再按像素宽度分层，层数决定曲线区往下挪多少
  const labels = useMemo(() => {
    const groups = groupKeyDateMarks(keyDateMarks(keyDates, windowFrom, length))
    const items = groups.map((group) => {
      const text = `${group.keys.map((key) => keyDateName(t, key)).join(' · ')} ${formatDayRange(group.firstDate, group.lastDate)}`
      const left = seriesX(group.start, length, LEFT, plotWidth)
      const right = seriesX(group.end, length, LEFT, plotWidth)
      return { group, text, center: (left + right) / 2, width: estimateTextWidth(text, LABEL_FONT) + 12 }
    })
    const layout = layoutLabelRows(items, LEFT, LEFT + plotWidth, 6)
    return { items, slots: layout.slots, rowCount: layout.rowCount }
  }, [keyDates, windowFrom, length, plotWidth, t])

  const salesTop = LABEL_TOP + labels.rowCount * LABEL_ROW_H + 10
  const salesScale = useMemo(() => niceScale(Math.max(0, ...sales), 4), [sales])
  const salesY = (value: number) => salesTop + SALES_H - (Math.max(0, value) / salesScale.max) * SALES_H
  const buyTotals = useMemo(() => warehouse.map((value, index) => value + (supplier[index] ?? 0)), [warehouse, supplier])
  const buyScale = useMemo(() => niceScale(Math.max(0, ...buyTotals), 2), [buyTotals])
  const buyTop = salesTop + SALES_H + CAPTION_H
  const buyY = (value: number) => buyTop + BUY_H - (Math.max(0, value) / buyScale.max) * BUY_H
  const height = buyTop + BUY_H + AXIS_H
  const barWidth = Math.min(10, Math.max(3, dayWidth * 0.7))

  const peak = useMemo(() => findPeak(sales), [sales])
  const discountRects = useMemo(
    () =>
      rangesToIndexSpans(discountRanges, windowFrom, length).map((span) => {
        const x1 = Math.max(LEFT, seriesX(span.start, length, LEFT, plotWidth) - dayWidth / 2)
        const x2 = Math.min(LEFT + plotWidth, seriesX(span.end, length, LEFT, plotWidth) + dayWidth / 2)
        return { x: x1, width: Math.max(1, x2 - x1) }
      }),
    [discountRanges, windowFrom, length, plotWidth, dayWidth],
  )
  const months = useMemo(() => monthStartTicks(windowFrom, length), [windowFrom, length])

  const salesTotal = sales.reduce((sum, value) => sum + value, 0)
  const buyTotal = buyTotals.reduce((sum, value) => sum + value, 0)
  const linePoints = sales.map((value, index) => `${x(index).toFixed(1)},${salesY(value).toFixed(1)}`)
  const linePath = linePoints.length ? `M${linePoints.join(' L')}` : ''
  const areaPath = linePoints.length ? `${linePath} L${x(length - 1).toFixed(1)},${salesY(0)} L${x(0).toFixed(1)},${salesY(0)} Z` : ''

  const onPointerMove = (event: MouseEvent<SVGRectElement>) => {
    if (length === 0) return
    const box = event.currentTarget.getBoundingClientRect()
    // 感应区从第 0 天左侧半天开始（SVG 按 1:1 像素绘制），换算回最近的那一天
    const offset = event.clientX - box.left - dayWidth / 2
    setHover(Math.min(length - 1, Math.max(0, Math.round(length > 1 ? offset / dayWidth : 0))))
  }

  const tooltip = (() => {
    if (hover === null) return null
    const date = dateAtOffset(windowFrom, hover)
    const buy = buyTotals[hover] ?? 0
    const lines = [
      date || `#${hover + 1}`,
      t(`${NS}.expand.tipSales`, { qty: formatInteger(sales[hover]) }),
      discount[hover] > 0 ? t(`${NS}.expand.tipDiscount`, { qty: formatInteger(discount[hover]) }) : '',
      buy > 0
        ? t(`${NS}.expand.tipPurchase`, { qty: formatInteger(buy), warehouse: formatInteger(warehouse[hover]), supplier: formatInteger(supplier[hover]) })
        : '',
    ].filter(Boolean)
    return { x: x(hover), lines }
  })()

  const ariaLabel = t(`${NS}.expand.chartAria`, { sales: formatInteger(salesTotal), purchase: formatInteger(buyTotal) })

  return (
    <div ref={containerRef} className="sp-hist-chart" role="group" aria-label={ariaLabel}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} aria-hidden="true">
        {/* 折扣期粉色底（上下两块） */}
        {discountRects.map((rect, index) => (
          <g key={`d${index}`}>
            <rect x={rect.x} y={salesTop} width={rect.width} height={SALES_H} fill={HISTORY_COLORS.discount} />
            <rect x={rect.x} y={buyTop} width={rect.width} height={BUY_H} fill={HISTORY_COLORS.discount} opacity={0.6} />
          </g>
        ))}

        {/* 销量刻度 */}
        {salesScale.ticks.map((tick) => (
          <g key={`s${tick}`}>
            <line x1={LEFT} x2={LEFT + plotWidth} y1={salesY(tick)} y2={salesY(tick)} stroke={GRID} />
            <text x={LEFT - 6} y={salesY(tick) + 3} textAnchor="end" fontSize="10" fill={AXIS}>
              {formatInteger(tick)}
            </text>
          </g>
        ))}

        {/* 节日关键日期虚线：从各自标签底部一直画到进货区底部 */}
        {labels.items.map((item, index) => {
          const labelBottom = LABEL_TOP + labels.slots[index].row * LABEL_ROW_H + 16
          const indexes = []
          for (let day = item.group.start; day <= item.group.end; day += 1) indexes.push(day)
          return indexes.map((day) => (
            <line
              key={`k${index}-${day}`}
              x1={x(day)}
              x2={x(day)}
              y1={labelBottom}
              y2={buyTop + BUY_H}
              stroke={HISTORY_COLORS.keyDate}
              strokeWidth={1}
              strokeDasharray="3 3"
              opacity={0.7}
            />
          ))
        })}

        {/* 销量面积 + 折线 */}
        {length > 0 ? (
          <>
            <path d={areaPath} fill={HISTORY_COLORS.salesFill} />
            <path d={linePath} fill="none" stroke={HISTORY_COLORS.sales} strokeWidth={1.8} strokeLinejoin="round" strokeLinecap="round" />
          </>
        ) : null}
        {salesTotal === 0 ? (
          <text x={LEFT + plotWidth / 2} y={salesTop + SALES_H / 2} textAnchor="middle" fontSize="12" fill="#667085">
            {t(`${NS}.expand.noSales`)}
          </text>
        ) : null}

        {/* 峰值 */}
        {peak && hover === null ? (
          <g>
            <circle cx={x(peak.index)} cy={salesY(peak.value)} r={3.5} fill="#fff" stroke={HISTORY_COLORS.sales} strokeWidth={1.6} />
            <text
              x={x(peak.index) + (peak.index > length / 2 ? -7 : 7)}
              y={salesY(peak.value) + 4}
              textAnchor={peak.index > length / 2 ? 'end' : 'start'}
              fontSize="11"
              fontWeight="600"
              fill="#b45309"
              paintOrder="stroke"
              stroke="#fff"
              strokeWidth={3}
            >
              {t(`${NS}.expand.peak`, { qty: formatInteger(peak.value), date: formatMonthDay(dateAtOffset(windowFrom, peak.index)) })}
            </text>
          </g>
        ) : null}

        {/* 进货区 */}
        <text x={LEFT} y={salesTop + SALES_H + 15} fontSize="10" fill="#667085">
          {t(`${NS}.expand.purchaseAxis`)}
        </text>
        <line x1={LEFT} x2={LEFT + plotWidth} y1={buyY(0)} y2={buyY(0)} stroke={GRID} />
        {buyTotals.map((total, index) => {
          if (total <= 0) return null
          const warehouseH = (warehouse[index] / buyScale.max) * BUY_H
          const supplierH = (supplier[index] / buyScale.max) * BUY_H
          const left = x(index) - barWidth / 2
          return (
            <g key={`b${index}`}>
              {warehouse[index] > 0 ? <rect x={left} y={buyY(0) - warehouseH} width={barWidth} height={warehouseH} rx={1.5} fill={HISTORY_COLORS.warehouse} /> : null}
              {supplier[index] > 0 ? (
                <rect x={left} y={buyY(0) - warehouseH - supplierH} width={barWidth} height={supplierH} rx={1.5} fill={HISTORY_COLORS.supplier} />
              ) : null}
              <text x={x(index) + barWidth / 2 + 3} y={Math.max(buyTop + 9, buyY(0) - warehouseH - supplierH + 9)} fontSize="10" fill="#355576">
                {formatInteger(total)}
              </text>
            </g>
          )
        })}
        {buyTotal === 0 ? (
          <text x={LEFT + plotWidth / 2} y={buyTop + BUY_H / 2 + 4} textAnchor="middle" fontSize="12" fill="#667085">
            {t(`${NS}.expand.noPurchase`)}
          </text>
        ) : null}

        {/* 月份刻度 */}
        {months.map((tick) => (
          <text
            key={`m${tick.index}`}
            x={tick.index === 0 ? LEFT : x(tick.index)}
            y={buyTop + BUY_H + 15}
            textAnchor={tick.index === 0 ? 'start' : 'middle'}
            fontSize="10"
            fill={AXIS}
          >
            {tick.label}
          </text>
        ))}

        {/* 节日标签最后画：不透明底色盖住穿过它的其他虚线 */}
        {labels.items.map((item, index) => {
          const slot = labels.slots[index]
          const labelWidth = Math.min(item.width, plotWidth)
          const y = LABEL_TOP + slot.row * LABEL_ROW_H
          return (
            <g key={`l${index}`}>
              <rect x={slot.left} y={y} width={labelWidth} height={16} rx={3} fill="#f3efff" stroke="#c4b5fd" />
              <text x={slot.left + labelWidth / 2} y={y + 11.5} textAnchor="middle" fontSize={LABEL_FONT} fill="#6d28d9">
                {item.text}
              </text>
            </g>
          )
        })}

        {/* 悬停 */}
        {hover !== null ? (
          <>
            <line x1={x(hover)} x2={x(hover)} y1={salesTop} y2={buyTop + BUY_H} stroke="#9fb3c8" strokeDasharray="3 3" />
            <circle cx={x(hover)} cy={salesY(sales[hover])} r={4} fill={HISTORY_COLORS.sales} stroke="#fff" strokeWidth={2} />
          </>
        ) : null}
        <rect
          x={LEFT - dayWidth / 2}
          y={salesTop}
          width={plotWidth + dayWidth}
          height={buyTop + BUY_H - salesTop}
          fill="transparent"
          onMouseMove={(event) => onPointerMove(event)}
          onMouseLeave={() => setHover(null)}
        />
      </svg>
      {tooltip ? (
        <div
          className="sp-hist-chart-tip"
          role="status"
          style={tooltip.x > width * 0.6 ? { right: width - tooltip.x + 12, top: salesTop } : { left: tooltip.x + 12, top: salesTop }}
        >
          {tooltip.lines.map((line, index) => (
            <span key={index}>{line}</span>
          ))}
        </div>
      ) : null}
    </div>
  )
}
