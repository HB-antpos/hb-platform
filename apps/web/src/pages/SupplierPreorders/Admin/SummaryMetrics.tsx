// 订货汇总顶部指标条：已下单款数 / 合计箱数 / 金额 / 开启分店（含显示列数） / 分店进度 / 体积与装柜进度。
// 数字全部来自矩阵的 totals / counts / storeStatuses；保存期间由 applyPendingEdits 与保存结果即时刷新。
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrix } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { computeContainerProgress, countStoreProgress } from './summaryMatrix.logic'
import { formatCbm, formatInteger, formatMoney } from './supplierPreorderAdmin.logic'

/** 最多画几根「已满」的柜条，超出的合并成文字说明，避免体积特别大时把指标条撑坏 */
const MAX_FULL_BARS = 6

interface SummaryMetricsProps {
  matrix: SupplierPreorderMatrix
  containerType?: string | null
  containerCapacityCbm?: number | null
  /** 表格当前显示的分店列数（只是视图，不影响下面任何合计） */
  shownColumnCount: number
}

export default function SummaryMetrics({ matrix, containerType, containerCapacityCbm, shownColumnCount }: SummaryMetricsProps) {
  const { t } = useTranslation()
  const { totals, counts } = matrix
  const progress = useMemo(() => countStoreProgress(matrix.stores, matrix.storeStatuses), [matrix.stores, matrix.storeStatuses])
  const container = useMemo(() => computeContainerProgress(totals.totalCbm, containerCapacityCbm), [totals.totalCbm, containerCapacityCbm])
  const fullBars = Math.min(container.fullCount, MAX_FULL_BARS)
  const percent = Math.round(container.partialRatio * 100)

  const volumeTitle = container.fallback
    ? t('spAdmin.summary.volumeTitleFallback', { capacity: formatCbm(container.capacity) })
    : t('spAdmin.summary.volumeTitle', { container: containerType ? `${containerType} ` : '', capacity: formatCbm(container.capacity) })

  return (
    <div className="sp-admin-card sp-admin-sum-metrics sp-admin-number" role="group" aria-label={t('spAdmin.summary.metricsAria')}>
      <div className="sp-admin-sum-metric">
        <div className="sp-admin-sum-metric-label">{t('spAdmin.summary.metricOrderedItems')}</div>
        <div className="sp-admin-sum-metric-value">
          {formatInteger(totals.orderedItemCount)}
          <small>{t('spAdmin.summary.ofItems', { count: formatInteger(counts.total) })}</small>
        </div>
      </div>
      <div className="sp-admin-sum-metric">
        <div className="sp-admin-sum-metric-label">{t('spAdmin.summary.metricCartons')}</div>
        <div className="sp-admin-sum-metric-value">
          {formatInteger(totals.totalCartons)}
          <small>{t('spAdmin.summary.cartons')}</small>
        </div>
      </div>
      <div className="sp-admin-sum-metric">
        <div className="sp-admin-sum-metric-label">{t('spAdmin.summary.metricAmount')}</div>
        <div className="sp-admin-sum-metric-value">{formatMoney(totals.totalAmount)}</div>
      </div>
      <div className="sp-admin-sum-metric">
        <div className="sp-admin-sum-metric-label">{t('spAdmin.summary.metricEnabledStores')}</div>
        <div className="sp-admin-sum-metric-value">
          {formatInteger(matrix.stores.length)}
          <small>{t('spAdmin.summary.enabledStoresUnit', { shown: shownColumnCount })}</small>
        </div>
      </div>
      <div className="sp-admin-sum-metric is-progress">
        <div className="sp-admin-sum-metric-label">{t('spAdmin.summary.metricStores', { count: matrix.stores.length })}</div>
        <div className="sp-admin-sum-dots" role="img" aria-label={t('spAdmin.summary.storeProgressAria', { submitted: progress.submitted, draft: progress.draft, none: progress.none })}>
          {progress.segments.map((status, index) => (
            <i key={matrix.stores[index]?.storeGuid ?? index} className={`is-${status}`} title={`${matrix.stores[index]?.storeName ?? ''} · ${t(`spAdmin.common.orderStatus.${status}`)}`} />
          ))}
        </div>
        <div className="sp-admin-sum-legend">
          <span>
            <i className="sp-admin-sum-sw is-Submitted" />
            {t('spAdmin.summary.progressSubmitted', { count: progress.submitted })}
          </span>
          <span>
            <i className="sp-admin-sum-sw is-Draft" />
            {t('spAdmin.summary.progressDraft', { count: progress.draft })}
          </span>
          <span>
            <i className="sp-admin-sum-sw is-None" />
            {t('spAdmin.summary.progressNone', { count: progress.none })}
          </span>
        </div>
      </div>
      <div className="sp-admin-sum-metric is-volume">
        <div className="sp-admin-sum-volume-head">
          <span className="sp-admin-sum-metric-label">{volumeTitle}</span>
          <span className="sp-admin-strong">{t('spAdmin.summary.volumeValue', { cbm: formatCbm(totals.totalCbm), containers: container.containers.toFixed(2) })}</span>
        </div>
        <div className="sp-admin-sum-bars" role="img" aria-label={t('spAdmin.summary.volumeAria', { containers: container.containers.toFixed(2) })}>
          {Array.from({ length: fullBars }, (_, index) => (
            <span key={index} className="sp-admin-sum-bar">
              <b style={{ width: '100%' }} />
            </span>
          ))}
          <span className="sp-admin-sum-bar is-part">
            <b style={{ width: `${percent}%` }} />
          </span>
        </div>
        <div className="sp-admin-sum-legend">
          <span>{t('spAdmin.summary.containersFull', { count: container.fullCount })}</span>
          <span>{t('spAdmin.summary.containerPartial', { n: container.fullCount + 1, percent, rest: formatCbm(container.remainingCbm) })}</span>
        </div>
      </div>
    </div>
  )
}
