import { Switch } from 'antd'
import { useEffect, useId, useRef, useState, useSyncExternalStore } from 'react'
import { useTranslation } from 'react-i18next'

import { buildDiscountDailyChartModel, buildValueLabels, placeChartTooltip, type DiscountChartDaily, type DiscountChartKind, type DiscountChartPoint } from './chartModel'

interface DiscountDailyChartProps {
  data: DiscountChartDaily[]
  ariaLabel: string
  className?: string
  classificationUnavailable?: boolean
}

const colors: Record<DiscountChartKind, string> = { regular: '#1677ff', discount: '#fa8c16', unknown: '#aab2bd', total: '#1677ff' }
const audFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' })
const quantityFormatter = new Intl.NumberFormat('en-AU')
const TOOLTIP_WIDTH = 176
/** 三种提示框（未统计 / 仅总量 / 完整分类）的高度，与下方各自的行布局一致；完整版须在 x 轴日期胶囊（plotBottom + 5）之上结束。 */
const TOOLTIP_HEIGHT = { unavailable: 48, pending: 78, full: 150 } as const

// 「显示数值」偏好：两张趋势图共用。模块级订阅让任一图切换时另一张同步；默认打开，关掉后记在本机浏览器。
const VALUE_LABELS_STORAGE_KEY = 'hb.batch-product-sales.chart-value-labels'
const valueLabelListeners = new Set<() => void>()
let showValuesSnapshot: boolean | null = null
function getShowValues() {
  if (showValuesSnapshot === null) {
    try { showValuesSnapshot = window.localStorage.getItem(VALUE_LABELS_STORAGE_KEY) !== '0' } catch { showValuesSnapshot = true }
  }
  return showValuesSnapshot
}
function setShowValues(value: boolean) {
  showValuesSnapshot = value
  try { window.localStorage.setItem(VALUE_LABELS_STORAGE_KEY, value ? '1' : '0') } catch { /* 仅本次会话生效 */ }
  valueLabelListeners.forEach((listener) => listener())
}
function subscribeShowValues(listener: () => void) {
  valueLabelListeners.add(listener)
  return () => { valueLabelListeners.delete(listener) }
}

function compactDate(value: string) {
  return value.length >= 10 ? value.slice(5) : value
}
function priceRange(min: number | null, max: number | null) {
  if (min === null || max === null) return '—'
  return min === max ? audFormatter.format(min) : `${audFormatter.format(min)}–${audFormatter.format(max)}`
}
function weekdayLabel(date: string, locale: string) {
  const parsed = new Date(`${date}T00:00:00Z`)
  return Number.isNaN(parsed.valueOf()) ? '' : new Intl.DateTimeFormat(locale, { weekday: 'short', timeZone: 'UTC' }).format(parsed)
}
/** 日期胶囊宽度估算：中文按 11px，数字约 6.6px，其他字符约 4px，左右各留 8px。 */
function pillWidth(text: string) {
  let width = 16
  for (const char of text) width += /[　-鿿]/.test(char) ? 11 : /\d/.test(char) ? 6.6 : 4
  return width
}
function tooltipKind(point: DiscountChartPoint) {
  return point.unavailable ? 'unavailable' : point.pending ? 'pending' : 'full'
}

export default function DiscountDailyChart({ data, ariaLabel, className, classificationUnavailable = false }: DiscountDailyChartProps) {
  const { t, i18n } = useTranslation()
  const [activeIndex, setActiveIndex] = useState<number | null>(null)
  const showValues = useSyncExternalStore(subscribeShowValues, getShowValues, () => true)
  const shadowId = `batch-chart-shadow-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
  const containerRef = useRef<HTMLDivElement>(null)
  const [chartWidth, setChartWidth] = useState(720)
  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const updateWidth = () => setChartWidth(Math.max(260, Math.round(container.getBoundingClientRect().width)))
    updateWidth()
    const observer = new ResizeObserver(updateWidth)
    observer.observe(container)
    return () => observer.disconnect()
  }, [data.length])
  // 两栏都用同一绘图区高度；窄一些的分店图按真实容器宽度重新计算坐标，不压扁文字或柱形。
  const model = buildDiscountDailyChartModel(data, chartWidth, 186, classificationUnavailable)
  const quantityOnly = model.points.some((point) => point.pending)
  const discountsPending = model.points.some((point) => point.discountPending)
  const active = activeIndex === null ? null : model.points[activeIndex] ?? null
  const activeKind = active ? tooltipKind(active) : null
  // 提示框先放右侧、放不下翻到左侧，不再夹回到当前列上（旧逻辑会盖住光标导致 mouseleave 循环闪烁）。
  const tooltip = active && activeKind && activeIndex !== null ? placeChartTooltip(model, activeIndex, { width: TOOLTIP_WIDTH, height: TOOLTIP_HEIGHT[activeKind] }) : null
  const labels = showValues ? buildValueLabels(model, { avoid: tooltip }) : []
  const locale = i18n.language?.startsWith('en') ? 'en-AU' : 'zh-CN'
  const pillText = active ? `${compactDate(active.date)} ${weekdayLabel(active.date, locale)}`.trim() : ''
  const pillW = pillWidth(pillText)
  const pillX = active ? Math.min(Math.max(active.x - pillW / 2, 2), model.width - pillW - 2) : 0

  if (!data.length) return null

  const tx = tooltip?.x1 ?? 0
  const ty = tooltip?.y1 ?? 0
  const valueRow = (y: number, name: string, value: string, color: string, key: string) => (
    <g key={key}>
      <line x1={tx + 12} x2={tx + 22} y1={y - 4} y2={y - 4} stroke={color} strokeWidth="2.5" strokeLinecap="round" />
      <text x={tx + 28} y={y} fontSize="11" fill="#c9d3e1">{name}</text>
      <text x={tx + TOOLTIP_WIDTH - 12} y={y} fontSize="11.5" fontWeight="500" textAnchor="end" fill="#fff" className="batch-product-sales-chart-number">{value}</text>
    </g>
  )
  const tooltipHeader = active ? (
    <>
      <text x={tx + 12} y={ty + 17} fontSize="12" fontWeight="600" fill="#fff">{pillText}</text>
      <text x={tx + TOOLTIP_WIDTH - 12} y={ty + 17} fontSize="11" textAnchor="end" fill="#8a97ab">{active.date.slice(0, 4)}</text>
    </>
  ) : null
  const quantityHeadline = active ? (
    <>
      <text x={tx + 12} y={ty + 40} fontSize="11" fill="#8a97ab">{t('batchProductSalesAnalysis.metrics.quantity')}</text>
      <text x={tx + TOOLTIP_WIDTH - 12} y={ty + 41} fontSize="17" fontWeight="700" textAnchor="end" fill="#fff" className="batch-product-sales-chart-number">{quantityFormatter.format(active.quantity)}</text>
      <line x1={tx + 12} x2={tx + TOOLTIP_WIDTH - 12} y1={ty + 49} y2={ty + 49} stroke="#2c3a52" />
    </>
  ) : null

  return (
    <div ref={containerRef} className={className}>
      <div className="batch-product-sales-chart-toolbar">
        <div className="batch-product-sales-chart-legend" aria-label={t('batchProductSalesAnalysis.chart.legend')}>
          {(quantityOnly ? ['total'] as const : ['regular', 'discount', 'unknown'] as const).map((kind) => <span key={kind}><i style={{ background: colors[kind] }} />{t(`batchProductSalesAnalysis.metrics.${kind === 'total' ? 'quantity' : kind}`)}</span>)}
        </div>
        <label className="batch-product-sales-chart-values-toggle">
          <Switch size="small" checked={showValues} onChange={setShowValues} />
          {t('batchProductSalesAnalysis.chart.showValues')}
        </label>
      </div>
      {/* 不放 <title>：浏览器会把它当原生提示弹出并盖住日期轴；读屏名称由 aria-label 提供。 */}
      <svg role="img" aria-label={ariaLabel} viewBox={`0 0 ${model.width} ${model.height}`} className="batch-product-sales-chart">
        <desc>{t(quantityOnly ? (discountsPending ? 'batchProductSalesAnalysis.chart.quantityDescription' : 'batchProductSalesAnalysis.chart.unavailableDescription') : 'batchProductSalesAnalysis.chart.description')}</desc>
        <defs>
          <filter id={shadowId} x="-20%" y="-20%" width="140%" height="150%"><feDropShadow dx="0" dy="4" stdDeviation="5" floodColor="#0b1220" floodOpacity=".22" /></filter>
        </defs>
        {active ? <rect x={active.hitX} y={model.plotTop - 4} width={active.hitWidth} height={model.plotBottom - model.plotTop + 4} rx="3" fill="#1677ff" opacity=".09" pointerEvents="none" /> : null}
        {model.ticks.map((tick) => <g key={tick.value}><line x1={model.plotLeft} x2={model.plotRight} y1={tick.y} y2={tick.y} stroke={tick.value === 0 ? '#8590a2' : '#edf0f5'} strokeDasharray={tick.value === 0 ? '4 3' : '2 2'} /><text x={model.plotLeft - 7} y={tick.y + 4} textAnchor="end" fontSize="11" fill="#718096">{tick.value}</text></g>)}
        {model.weekDividers.map((divider) => <line key={divider.date} x1={divider.x} x2={divider.x} y1={model.plotTop} y2={model.plotBottom} stroke="#e2e8f0" strokeWidth="1" />)}
        {model.points.map((point, index) => (
          <g
            key={point.date}
            tabIndex={0}
            role="graphics-symbol"
            aria-label={t(point.unavailable ? 'batchProductSalesAnalysis.chart.uncomputedPointAria' : point.pending ? (point.discountPending ? 'batchProductSalesAnalysis.chart.totalPointAria' : 'batchProductSalesAnalysis.chart.unavailablePointAria') : 'batchProductSalesAnalysis.chart.pointAria', { date: point.date, quantity: point.quantity, amount: audFormatter.format(point.salesAmount), regular: point.regularQuantity, discount: point.discountQuantity, unknown: point.unknownQuantity })}
            onFocus={() => setActiveIndex(index)}
            onBlur={() => setActiveIndex((current) => current === index ? null : current)}
            onMouseEnter={() => setActiveIndex(index)}
            onMouseLeave={() => setActiveIndex((current) => current === index ? null : current)}
          >
            {/* 悬停某天时其余柱淡化，突出当前列。 */}
            {point.segments.map((segment) => segment.height > 0 ? <rect key={segment.kind} x={segment.x} y={segment.y} width={segment.width} height={segment.height} rx="1" fill={colors[segment.kind]} opacity={activeIndex !== null && activeIndex !== index ? 0.55 : undefined} /> : null)}
            <rect x={point.hitX} y={model.plotTop} width={point.hitWidth} height={model.plotBottom - model.plotTop} fill="transparent" />
          </g>
        ))}
        {/* 悬停时日期胶囊附近的刻度文字让位，避免半截露在胶囊外。 */}
        {model.xTicks.map((tick) => active && Math.abs(tick.x - active.x) < pillW / 2 + 20 ? null : <text key={tick.date} x={tick.x} y={model.plotBottom + 18} textAnchor="middle" fontSize="11" fill="#718096">{compactDate(tick.date)}</text>)}
        {/* 以下叠加层全部不接收鼠标，只有上面的日期槽热区响应悬停，避免叠加层抢走指针造成闪烁。 */}
        {labels.length ? (
          <g className="batch-product-sales-chart-values" aria-hidden="true" pointerEvents="none">
            {labels.map((label) => {
              const strong = label.peak || label.index === activeIndex
              return <text key={label.index} x={label.x} y={label.y} textAnchor="middle" fontSize="10" fontWeight={strong ? 700 : 500} fill={strong ? '#1f2a3d' : '#5b6b82'} className="batch-product-sales-chart-number">{label.text}</text>
            })}
          </g>
        ) : null}
        {active ? (
          <g aria-hidden="true" pointerEvents="none">
            <rect x={pillX} y={model.plotBottom + 5} width={pillW} height="19" rx="9.5" fill="#1677ff" />
            <text x={pillX + pillW / 2} y={model.plotBottom + 18} textAnchor="middle" fontSize="11" fontWeight="600" fill="#fff">{pillText}</text>
          </g>
        ) : null}
        {active && tooltip ? (
          <g aria-live="polite" pointerEvents="none" filter={`url(#${shadowId})`}>
            <rect x={tx} y={ty} width={TOOLTIP_WIDTH} height={tooltip.y2 - tooltip.y1} rx="6" fill="#172033" />
            {tooltipHeader}
            {activeKind === 'unavailable' ? (
              <text x={tx + 12} y={ty + 37} fontSize="11" fill="#ffd591">{t('batchProductSalesAnalysis.chart.uncomputed')}</text>
            ) : (
              <>
                {quantityHeadline}
                {activeKind === 'full' ? [
                  valueRow(ty + 64, t('batchProductSalesAnalysis.metrics.regular'), quantityFormatter.format(active.regularQuantity), colors.regular, 'regular'),
                  valueRow(ty + 79, t('batchProductSalesAnalysis.metrics.discount'), quantityFormatter.format(active.discountQuantity), colors.discount, 'discount'),
                  valueRow(ty + 94, t('batchProductSalesAnalysis.metrics.unknown'), quantityFormatter.format(active.unknownQuantity), colors.unknown, 'unknown'),
                ] : null}
                <text x={tx + 12} y={ty + (activeKind === 'full' ? 110 : 67)} fontSize="11" fill="#9fe0c9">{t('batchProductSalesAnalysis.metrics.amount')}</text>
                <text x={tx + TOOLTIP_WIDTH - 12} y={ty + (activeKind === 'full' ? 110 : 67)} fontSize="11.5" fontWeight="600" textAnchor="end" fill="#9fe0c9" className="batch-product-sales-chart-number">{audFormatter.format(active.salesAmount)}</text>
                {activeKind === 'full' ? (
                  <>
                    <text x={tx + 12} y={ty + 126} fontSize="10.5" fill="#a9b6c8">{t('batchProductSalesAnalysis.chart.originalPrice')}</text>
                    <text x={tx + TOOLTIP_WIDTH - 12} y={ty + 126} fontSize="10.5" textAnchor="end" fill="#dfe7f1">{priceRange(active.originalPriceMin, active.originalPriceMax)}</text>
                    <text x={tx + 12} y={ty + 141} fontSize="10.5" fill="#a9b6c8">{t('batchProductSalesAnalysis.chart.discountPrice')}</text>
                    <text x={tx + TOOLTIP_WIDTH - 12} y={ty + 141} fontSize="10.5" textAnchor="end" fill="#dfe7f1">{priceRange(active.discountPriceMin, active.discountPriceMax)}</text>
                  </>
                ) : null}
              </>
            )}
          </g>
        ) : null}
      </svg>
    </div>
  )
}
