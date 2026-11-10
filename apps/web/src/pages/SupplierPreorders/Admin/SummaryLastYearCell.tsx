// 订货汇总矩阵里的「去年全店」格：缩略走势 + 销量件数 + 售罄率；借用参考商品时整格浅紫并标「参考 XXX」；没有记录显示「无去年记录」。
import { memo, useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixLastYear } from '../../../types/supplierPreorder'
import { expandDaily, formatPercent, resolveSeriesLength, sellThroughTone, sparklinePaths } from '../Ordering/orderingHistory.logic'
import { HISTORY_COLORS } from '../Ordering/orderingHistoryCells'

import './registerSpAdminMessages'
import { formatInteger } from './supplierPreorderAdmin.logic'

const SPARK_WIDTH = 56
const SPARK_HEIGHT = 20

interface SummaryLastYearCellProps {
  lastYear?: SupplierPreorderMatrixLastYear | null
  /** 去年历史窗口天数（日销数组长度） */
  windowDays: number | null
  /** 历史快照还没算好：没有数据时显示「计算中」而不是「无去年记录」 */
  pending: boolean
}

export function lastYearCellClassName(lastYear?: SupplierPreorderMatrixLastYear | null): string {
  return lastYear?.source === 'Ref' ? 'sp-admin-sum-ly-td is-ref' : 'sp-admin-sum-ly-td'
}

export const SummaryLastYearCell = memo(function SummaryLastYearCell({ lastYear, windowDays, pending }: SummaryLastYearCellProps) {
  const { t } = useTranslation()
  const paths = useMemo(() => {
    if (!lastYear) return null
    const length = resolveSeriesLength(windowDays, lastYear.daily)
    return sparklinePaths(expandDaily(lastYear.daily, length).sales, SPARK_WIDTH, SPARK_HEIGHT, { top: 2 })
  }, [lastYear, windowDays])

  if (!lastYear || !paths) {
    return <div className="sp-admin-sum-ly-empty">{pending ? t('spAdmin.summary.lastYearPending') : t('spAdmin.summary.lastYearNone')}</div>
  }
  const tone = sellThroughTone(lastYear.sellThrough)
  const refCode = lastYear.refDisplayCode?.trim() || lastYear.productCode
  return (
    <div className="sp-admin-sum-ly sp-admin-number">
      <svg className="sp-admin-sum-spark" width={SPARK_WIDTH} height={SPARK_HEIGHT} viewBox={`0 0 ${SPARK_WIDTH} ${SPARK_HEIGHT}`} aria-hidden="true">
        {paths.line ? (
          <>
            <path d={paths.area} fill={HISTORY_COLORS.salesFill} />
            <path d={paths.line} fill="none" stroke={HISTORY_COLORS.sales} strokeWidth={1.3} strokeLinejoin="round" strokeLinecap="round" />
          </>
        ) : null}
      </svg>
      <div className="sp-admin-sum-ly-text">
        <b>{t('spAdmin.summary.lastYearPieces', { count: formatInteger(lastYear.salesQty) })}</b>
        <span className={tone === 'good' ? 'sp-admin-sum-rate is-good' : tone === 'low' ? 'sp-admin-sum-rate is-low' : 'sp-admin-sum-rate'}>
          {formatPercent(lastYear.sellThrough)}
        </span>
      </div>
      {lastYear.source === 'Ref' ? <span className="sp-admin-sum-ref-tag">{t('spAdmin.summary.refTag', { code: refCode })}</span> : null}
    </div>
  )
})
