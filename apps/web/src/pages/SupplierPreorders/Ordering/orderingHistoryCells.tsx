// 下单表格里「去年本店」四列的格子组件：进货 / 销售 / 日销迷你曲线 / 折扣，
// 以及参考商品悬停卡片、无数据或计算中的占位。纯展示，数据都来自列表接口的 row.history。
import { Tag, Tooltip } from 'antd'
import type { TFunction } from 'i18next'
import { memo, useMemo, type ReactNode } from 'react'

import type {
  SupplierPreorderItemHistory,
  SupplierPreorderItemRef,
  SupplierPreorderKeyDate,
  SupplierPreorderKeyDateKey,
} from '../../../types/supplierPreorder'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'

import {
  expandDaily,
  formatDateSpan,
  formatPercent,
  keyDateMarks,
  rangesToIndexSpans,
  refDisplayCode,
  resolveSeriesLength,
  sellThroughTone,
  seriesX,
  sparklinePaths,
  type HistoryCellState,
} from './orderingHistory.logic'
import { formatAud, formatInteger } from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

/** 曲线配色：与「澳洲本地商品分析」一致，销量橙色；节日紫色虚线；折扣期粉色底 */
export const HISTORY_COLORS = {
  sales: '#e8710a',
  salesFill: 'rgba(232, 113, 10, .14)',
  keyDate: '#8b5cf6',
  discount: '#fde4ea',
  warehouse: '#5b8fd9',
  supplier: '#3aa57a',
} as const

const SPARK_WIDTH = 120
const SPARK_HEIGHT = 40

/** 节日关键日期名称（已翻译） */
export function keyDateName(t: TFunction, key: SupplierPreorderKeyDateKey): string {
  return t(`${NS}.history.keyDate.${key}`, { defaultValue: key })
}

/** 售罄率文字颜色 class：≥90% 绿、<60% 暖色 */
export function sellThroughClassName(ratio: number | null | undefined): string {
  const tone = sellThroughTone(ratio)
  return tone === 'good' ? 'sp-hist-rate is-good' : tone === 'low' ? 'sp-hist-rate is-low' : 'sp-hist-rate'
}

// ---------- 参考商品 ----------

/** 参考商品悬停卡片：名称、缩略图，以及「参考进价 → 本品进价」 */
export function RefTooltip({
  t,
  refItem,
  ownPrice,
  fallbackCode,
  children,
}: {
  t: TFunction
  refItem?: SupplierPreorderItemRef | null
  ownPrice: number
  fallbackCode?: string | null
  children: ReactNode
}) {
  const code = refDisplayCode(refItem, fallbackCode)
  const thumb = toProductThumbnailUrl(refItem?.imageUrl, 96)
  const sourceLabel =
    refItem?.source === 'Manual' ? t(`${NS}.history.refSourceManual`) : refItem?.source === 'Auto' ? t(`${NS}.history.refSourceAuto`) : null
  const content = (
    <div className="sp-hist-ref-tip">
      {thumb ? <img src={thumb} alt="" width={48} height={48} loading="lazy" /> : <span className="sp-hist-ref-tip-empty" />}
      <div className="sp-hist-ref-tip-body">
        <div className="sp-hist-ref-tip-title">
          {t(`${NS}.history.refTitle`)} {code}
          {sourceLabel ? <span className="sp-hist-ref-tip-source">{sourceLabel}</span> : null}
        </div>
        {refItem?.name ? <div className="sp-hist-ref-tip-name">{refItem.name}</div> : null}
        {refItem?.purchasePrice !== null && refItem?.purchasePrice !== undefined ? (
          <div className="sp-hist-ref-tip-price">
            {t(`${NS}.history.refPrice`, { ref: formatAud(refItem.purchasePrice), own: formatAud(ownPrice) })}
          </div>
        ) : null}
        <div className="sp-hist-ref-tip-note">{t(`${NS}.history.refHint`)}</div>
      </div>
    </div>
  )
  return (
    <Tooltip title={content} color="#fff" styles={{ body: { color: '#1f2430' } }} mouseEnterDelay={0.15}>
      {children}
    </Tooltip>
  )
}

/** 紫色小标签「参考 XD0001342」，悬停看参考商品详情 */
export function RefBadge({ t, history, ownPrice }: { t: TFunction; history: SupplierPreorderItemHistory; ownPrice: number }) {
  const code = refDisplayCode(history.ref, history.productCode)
  return (
    <RefTooltip t={t} refItem={history.ref} ownPrice={ownPrice} fallbackCode={history.productCode}>
      <span className="sp-hist-ref-badge" tabIndex={0}>
        {t(`${NS}.table.refTag`, { code })}
      </span>
    </RefTooltip>
  )
}

// ---------- 占位 ----------

/** 四格合并后的占位：无去年记录（虚线框）/ 本店去年无记录 / 计算中 / 不可用 */
export function HistoryPlaceholder({ t, state, canViewOtherStores }: { t: TFunction; state: HistoryCellState; canViewOtherStores: boolean }) {
  if (state === 'pending') return <div className="sp-hist-empty is-pending">{t(`${NS}.history.pending`)}</div>
  if (state === 'failed') return <div className="sp-hist-empty">{t(`${NS}.history.failed`)}</div>
  if (state === 'noStore') {
    return (
      <div className="sp-hist-empty">
        {t(`${NS}.history.noStore`)}
        {canViewOtherStores ? <span className="sp-hist-empty-hint"> · {t(`${NS}.history.noStoreExpand`)}</span> : null}
      </div>
    )
  }
  return <div className="sp-hist-empty">{t(`${NS}.history.none`)}</div>
}

// ---------- 四列格子 ----------

export function PurchaseCell({ t, history }: { t: TFunction; history: SupplierPreorderItemHistory }) {
  return (
    <div className="sp-hist-cell">
      <div className="sp-hist-span">{formatDateSpan(history.purchaseFirst, history.purchaseLast)}</div>
      <div className="sp-hist-sub">
        {history.purchaseBatches > 0 || history.purchaseQty > 0
          ? t(`${NS}.history.purchaseFigures`, { batches: formatInteger(history.purchaseBatches), qty: formatInteger(history.purchaseQty) })
          : t(`${NS}.history.noPurchase`)}
      </div>
    </div>
  )
}

export function SalesCell({ t, history }: { t: TFunction; history: SupplierPreorderItemHistory }) {
  const hasRate = history.sellThrough !== null && history.sellThrough !== undefined
  return (
    <div className="sp-hist-cell">
      <div className="sp-hist-span">{formatDateSpan(history.salesFirst, history.salesLast)}</div>
      <div className="sp-hist-sub">
        {formatInteger(history.salesQty)} {t(`${NS}.history.pcs`)}
        {hasRate ? (
          <>
            {' · '}
            {t(`${NS}.history.sellThroughShort`)} <b className={sellThroughClassName(history.sellThrough)}>{formatPercent(history.sellThrough)}</b>
          </>
        ) : null}
      </div>
    </div>
  )
}

export function DiscountCell({ t, history }: { t: TFunction; history: SupplierPreorderItemHistory }) {
  if (history.discountStatus === 'unknown') {
    return (
      <div className="sp-hist-cell">
        <Tag className="sp-hist-tag">{t(`${NS}.history.discountUnknown`)}</Tag>
      </div>
    )
  }
  return (
    <div className="sp-hist-cell">
      {history.hasDiscount ? (
        <Tag color="red" className="sp-hist-tag">
          {t(`${NS}.history.hasDiscount`)}
        </Tag>
      ) : (
        <Tag color="green" className="sp-hist-tag">
          {t(`${NS}.history.noDiscount`)}
        </Tag>
      )}
      {history.hasDiscount && history.discountShare !== null && history.discountShare !== undefined ? (
        <div className="sp-hist-sub">{t(`${NS}.history.discountShare`, { rate: formatPercent(history.discountShare) })}</div>
      ) : null}
      {history.discountStatus === 'partial' ? <div className="sp-hist-sub is-warn">{t(`${NS}.history.discountPartial`)}</div> : null}
    </div>
  )
}

interface SparklineProps {
  t: TFunction
  history: SupplierPreorderItemHistory
  windowFrom?: string | null
  windowDays: number | null
  keyDates: readonly SupplierPreorderKeyDate[]
}

/** 去年日销迷你曲线（纯 SVG 120×40）：橙色折线 + 面积；紫色虚线 = 节日关键日期；粉色底 = 折扣期 */
export const HistorySparkline = memo(function HistorySparkline({ t, history, windowFrom, windowDays, keyDates }: SparklineProps) {
  const model = useMemo(() => {
    const length = resolveSeriesLength(windowDays, history.daily)
    const { sales } = expandDaily(history.daily, length)
    const paths = sparklinePaths(sales, SPARK_WIDTH, SPARK_HEIGHT, { top: 3 })
    // 每天的宽度：折扣区间两端各外扩半天，单日折扣也看得见
    const half = length > 1 ? SPARK_WIDTH / (length - 1) / 2 : SPARK_WIDTH / 2
    const discounts = rangesToIndexSpans(history.discountRanges, windowFrom, length).map((span) => {
      const x1 = Math.max(0, seriesX(span.start, length, 0, SPARK_WIDTH) - half)
      const x2 = Math.min(SPARK_WIDTH, seriesX(span.end, length, 0, SPARK_WIDTH) + half)
      return { x: x1, width: Math.max(1, x2 - x1) }
    })
    const marks = keyDateMarks(keyDates, windowFrom, length).map((mark) => seriesX(mark.index, length, 0, SPARK_WIDTH))
    const total = sales.reduce((sum, value) => sum + value, 0)
    return { paths, discounts, marks, total, length }
  }, [history, windowFrom, windowDays, keyDates])

  return (
    <svg
      className="sp-hist-spark"
      width={SPARK_WIDTH}
      height={SPARK_HEIGHT}
      viewBox={`0 0 ${SPARK_WIDTH} ${SPARK_HEIGHT}`}
      role="img"
      aria-label={t(`${NS}.history.sparkLabel`, { qty: formatInteger(model.total) })}
    >
      {model.discounts.map((rect, index) => (
        <rect key={`d${index}`} x={rect.x} y={0} width={rect.width} height={SPARK_HEIGHT} fill={HISTORY_COLORS.discount} />
      ))}
      {model.marks.map((x, index) => (
        <line key={`k${index}`} x1={x} x2={x} y1={0} y2={SPARK_HEIGHT} stroke={HISTORY_COLORS.keyDate} strokeWidth={1} strokeDasharray="2 2" opacity={0.75} />
      ))}
      {model.length > 0 ? (
        <>
          <path d={model.paths.area} fill={HISTORY_COLORS.salesFill} />
          <path d={model.paths.line} fill="none" stroke={HISTORY_COLORS.sales} strokeWidth={1.3} strokeLinejoin="round" strokeLinecap="round" />
        </>
      ) : null}
    </svg>
  )
})
