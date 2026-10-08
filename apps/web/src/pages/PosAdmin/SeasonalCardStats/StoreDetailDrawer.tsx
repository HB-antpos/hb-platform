import { Alert, Button, Drawer, Skeleton } from 'antd'
import { useTranslation } from 'react-i18next'

import type {
  SeasonalCardPriceOption,
  SeasonalCardStatsStoreDetail,
  SeasonalCardStatsStoreRow,
} from '../../../types/seasonalCardStats'

import {
  EMPTY_VALUE,
  PRICE_OPTIONS,
  buildSupplierMatrix,
  buildYearComparison,
  formatBatchLines,
  formatCount,
  formatLocalTime,
  formatMoney,
  formatSignedCount,
  getPriceLabel,
  type StatsErrorKind,
} from './logic'

export interface StoreDetailDrawerProps {
  open: boolean
  storeCode: string | null
  /** 表格里的同一行：明细加载期间先用它显示标题与状态，避免抽屉先空白。 */
  preview: SeasonalCardStatsStoreRow | null
  detail: SeasonalCardStatsStoreDetail | null
  loading: boolean
  errorKind: StatsErrorKind | null
  seasonYear: number
  /** 已翻译的节日名称 */
  holidayLabel: string
  onClose: () => void
  onRetry: () => void
}

function DetailBody({
  detail,
  seasonYear,
}: {
  detail: SeasonalCardStatsStoreDetail
  seasonYear: number
}) {
  const { t } = useTranslation()
  const otherLabel = t('seasonalCardStats.price.other')
  const priceLabel = (option: SeasonalCardPriceOption) => getPriceLabel(option, otherLabel)
  const unassigned = t('seasonalCardStats.unassignedSupplier')
  const matrix = buildSupplierMatrix(detail.currentBatches, unassigned)
  const comparison = buildYearComparison(detail.totalQuantity, detail.previousYearTotalQuantity)
  const lineLabels = {
    other: otherLabel,
    unitPrice: (price: string) => t('seasonalCardStats.drawer.unitPriceSuffix', { price }),
  }

  return (
    <div className="seasonal-card-stats-detail">
      <div className="seasonal-card-stats-detail-totals">
        <div>
          <small>{t('seasonalCardStats.drawer.remainingQuantity')}</small>
          <strong>
            {detail.isFilled
              ? t('seasonalCardStats.byPrice.quantity', { count: formatCount(detail.totalQuantity) })
              : EMPTY_VALUE}
          </strong>
        </div>
        <div>
          <small>{t('seasonalCardStats.drawer.remainingAmount')}</small>
          <strong>{detail.isFilled ? formatMoney(detail.totalAmount) : EMPTY_VALUE}</strong>
        </div>
      </div>

      <section className="seasonal-card-stats-section" aria-labelledby="scs-sec-matrix">
        <h5 id="scs-sec-matrix">{t('seasonalCardStats.drawer.matrixTitle')}</h5>
        {matrix.length ? (
          <div className="seasonal-card-stats-mini-scroll">
            <table className="seasonal-card-stats-mini-table">
              <thead>
                <tr>
                  <th scope="col">{t('seasonalCardStats.drawer.supplier')}</th>
                  {PRICE_OPTIONS.map((option) => (
                    <th key={option} scope="col" className="is-num">
                      {priceLabel(option)}
                    </th>
                  ))}
                  <th scope="col" className="is-num">
                    {t('seasonalCardStats.drawer.rowTotal')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {matrix.map((row) => (
                  <tr key={row.key}>
                    <th scope="row">{row.supplierName}</th>
                    {PRICE_OPTIONS.map((option) => {
                      const cell = row.cells[option]
                      return (
                        <td key={option} className={`is-num${cell.quantity === 0 ? ' is-zero' : ''}`}>
                          {formatCount(cell.quantity)}
                          {cell.unitPrice !== null ? (
                            <small className="seasonal-card-stats-unit">
                              {t('seasonalCardStats.drawer.atPrice', { price: formatMoney(cell.unitPrice) })}
                            </small>
                          ) : null}
                        </td>
                      )
                    })}
                    <td className="is-num">
                      <b>{formatCount(row.totalQuantity)}</b>
                      <small className="seasonal-card-stats-unit">{formatMoney(row.totalAmount)}</small>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="seasonal-card-stats-placeholder" role="note">
            {t('seasonalCardStats.drawer.notFilled')}
          </div>
        )}
      </section>

      <section className="seasonal-card-stats-section" aria-labelledby="scs-sec-history">
        <h5 id="scs-sec-history">{t('seasonalCardStats.drawer.historyTitle')}</h5>
        {detail.history.length ? (
          <ol className="seasonal-card-stats-timeline">
            {detail.history.map((batch, index) => (
              <li key={batch.batchGuid ?? `${batch.submittedAt}-${index}`} className={batch.isCurrent ? 'is-current' : 'is-replaced'}>
                <div className="seasonal-card-stats-timeline-head">
                  <span title={formatLocalTime(batch.submittedAt, 'full')}>{formatLocalTime(batch.submittedAt, 'short')}</span>
                  <span className={`seasonal-card-stats-tag ${batch.isCurrent ? 'is-filled' : 'is-muted'}`}>
                    {batch.isCurrent ? t('seasonalCardStats.drawer.current') : t('seasonalCardStats.drawer.replaced')}
                  </span>
                </div>
                <span className="seasonal-card-stats-timeline-meta">
                  {[batch.submittedByName || EMPTY_VALUE, batch.supplierName || batch.localSupplierCode || unassigned].join(' · ')}
                </span>
                <span className="seasonal-card-stats-timeline-lines">{formatBatchLines(batch.lines, lineLabels)}</span>
                {batch.remark ? (
                  <span className="seasonal-card-stats-timeline-meta">
                    {t('seasonalCardStats.drawer.remark', { remark: batch.remark })}
                  </span>
                ) : null}
              </li>
            ))}
          </ol>
        ) : (
          <div className="seasonal-card-stats-placeholder" role="note">
            {t('seasonalCardStats.drawer.noHistory')}
          </div>
        )}
      </section>

      <section className="seasonal-card-stats-section" aria-labelledby="scs-sec-compare">
        <h5 id="scs-sec-compare">{t('seasonalCardStats.drawer.compareTitle')}</h5>
        <div className="seasonal-card-stats-compare">
          <span>{seasonYear}</span>
          <span className="seasonal-card-stats-bar-track" aria-hidden="true">
            <span style={{ width: `${comparison.currentWidth}%` }} />
          </span>
          <span className="seasonal-card-stats-bar-value">
            {detail.isFilled ? t('seasonalCardStats.byPrice.quantity', { count: formatCount(detail.totalQuantity) }) : EMPTY_VALUE}
          </span>
          <span>{seasonYear - 1}</span>
          <span className="seasonal-card-stats-bar-track is-previous" aria-hidden="true">
            <span style={{ width: `${comparison.previousWidth}%` }} />
          </span>
          <span className="seasonal-card-stats-bar-value">
            {detail.previousYearTotalQuantity === null
              ? t('seasonalCardStats.drawer.previousNone')
              : t('seasonalCardStats.byPrice.quantity', { count: formatCount(detail.previousYearTotalQuantity) })}
          </span>
        </div>
        {comparison.delta !== null && detail.isFilled ? (
          <p className="seasonal-card-stats-muted seasonal-card-stats-compare-delta">
            {t('seasonalCardStats.drawer.compareDelta', { delta: formatSignedCount(comparison.delta) })}
          </p>
        ) : null}
      </section>
    </div>
  )
}

/** 单店明细抽屉（只读）：剩余数量/金额 → 供应商 × 价格矩阵 → 提交记录时间线 → 与去年同节日对比。 */
export default function StoreDetailDrawer({
  open,
  storeCode,
  preview,
  detail,
  loading,
  errorKind,
  seasonYear,
  holidayLabel,
  onClose,
  onRetry,
}: StoreDetailDrawerProps) {
  const { t } = useTranslation()
  const storeName = detail?.storeName || preview?.storeName || ''
  const isFilled = detail?.isFilled ?? preview?.isFilled

  const errorMessage: Record<StatsErrorKind, string> = {
    forbidden: t('seasonalCardStats.errors.forbidden'),
    notFound: t('seasonalCardStats.errors.storeNotFound'),
    invalidQuery: t('seasonalCardStats.errors.detailFailed'),
    failed: t('seasonalCardStats.errors.detailFailed'),
  }

  return (
    <Drawer
      rootClassName="seasonal-card-stats-drawer"
      open={open}
      width={560}
      destroyOnHidden
      onClose={onClose}
      closable={{ placement: 'end', 'aria-label': t('seasonalCardStats.drawer.close') }}
      title={
        <div className="seasonal-card-stats-drawer-title">
          <span>
            {storeName
              ? t('seasonalCardStats.drawer.title', { name: storeName, code: storeCode ?? '' })
              : storeCode ?? EMPTY_VALUE}
          </span>
          <span className="seasonal-card-stats-drawer-tags">
            <span className="seasonal-card-stats-tag is-muted">{seasonYear}</span>
            <span className="seasonal-card-stats-tag is-holiday">{holidayLabel}</span>
            {isFilled === undefined ? null : (
              <span className={`seasonal-card-stats-tag ${isFilled ? 'is-filled' : 'is-unfilled'}`}>
                {isFilled ? t('seasonalCardStats.status.filled') : t('seasonalCardStats.status.unfilled')}
              </span>
            )}
          </span>
        </div>
      }
      footer={
        <div className="seasonal-card-stats-drawer-footer">
          <Button onClick={onClose}>{t('seasonalCardStats.drawer.close')}</Button>
        </div>
      }
    >
      {errorKind ? (
        <Alert
          type={errorKind === 'notFound' ? 'warning' : 'error'}
          showIcon
          message={errorMessage[errorKind]}
          action={
            errorKind === 'failed' ? (
              <Button size="small" onClick={onRetry}>
                {t('seasonalCardStats.errors.retry')}
              </Button>
            ) : undefined
          }
        />
      ) : loading || !detail ? (
        <Skeleton active paragraph={{ rows: 10 }} aria-busy="true" />
      ) : (
        <DetailBody detail={detail} seasonYear={seasonYear} />
      )}
    </Drawer>
  )
}
