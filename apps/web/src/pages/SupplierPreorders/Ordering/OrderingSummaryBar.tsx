// 下单页指标条：本店款数 / 箱数 / 件数 / 金额 / 状态，以及全部分店体积占柜容量。
import { Skeleton } from 'antd'
import type { TFunction } from 'i18next'

import type { SupplierPreorderOrderingSummary } from '../../../types/supplierPreorder'

import { computeContainerRatio, formatAud, formatCbm, formatInteger, formatSydneyDateTime } from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

/** 各分店体积分段配色（循环使用） */
const SEGMENT_COLORS = ['#1677ff', '#13c2c2', '#52c41a', '#faad14', '#eb2f96', '#722ed1', '#fa8c16', '#2f54eb', '#a0d911', '#f5222d']

interface OrderingSummaryBarProps {
  t: TFunction
  summary: SupplierPreorderOrderingSummary | null
  containerType?: string | null
  containerCapacityCbm?: number | null
}

export default function OrderingSummaryBar({ t, summary, containerType, containerCapacityCbm }: OrderingSummaryBarProps) {
  if (!summary) {
    return (
      <div className="sp-order-metrics">
        <div className="sp-order-metric" style={{ gridColumn: '1 / -1' }}>
          <Skeleton active paragraph={false} title={{ width: '60%' }} />
        </div>
      </div>
    )
  }

  const ratio = computeContainerRatio(summary.allStoresCbm, containerCapacityCbm)
  const storeCbms = (summary.storeCbms ?? []).filter((entry) => entry.cbm > 0)
  // 分段宽度的分母：容量与实际总体积取大，超柜时整条铺满不溢出
  const barBase = ratio === null ? 0 : Math.max(containerCapacityCbm ?? 0, summary.allStoresCbm)

  return (
    <div className="sp-order-metrics">
      <Metric label={t(`${NS}.summary.selectedItems`)} value={formatInteger(summary.itemCount)} unit={t(`${NS}.summary.itemsUnit`)} />
      <Metric label={t(`${NS}.summary.cartons`)} value={formatInteger(summary.cartons)} unit={t(`${NS}.summary.cartonsUnit`)} />
      <Metric label={t(`${NS}.summary.pieces`)} value={formatInteger(summary.pieces)} unit={t(`${NS}.summary.piecesUnit`)} />
      <Metric label={t(`${NS}.summary.amount`)} value={formatAud(summary.amount)} />
      <div className="sp-order-metric">
        <div className="sp-order-metric-label">{t(`${NS}.summary.status`)}</div>
        <div className="sp-order-metric-status">
          {t(`${NS}.storeStatus.${summary.orderStatus}`)}
          <StatusDetail t={t} summary={summary} />
        </div>
      </div>
      <div className="sp-order-metric sp-order-metric-volume">
        <div className="sp-order-volume-head">
          <span className="sp-order-metric-label">
            {ratio === null
              ? t(`${NS}.summary.volumeTitleNoCapacity`)
              : t(`${NS}.summary.volumeTitle`, { container: containerType ? `${containerType} ` : '', capacity: formatInteger(containerCapacityCbm) })}
          </span>
          <span className="sp-order-volume-value">
            {t(`${NS}.summary.volumeValue`, { cbm: formatCbm(summary.allStoresCbm) })}
            {ratio === null ? null : ` · ${Math.round(ratio * 100)}%`}
          </span>
        </div>
        {ratio !== null ? (
          <div className="sp-order-volume-bar" role="img" aria-label={`${Math.round(ratio * 100)}%`}>
            {storeCbms.length ? (
              storeCbms.map((entry, index) => (
                <span
                  key={entry.storeName}
                  className="sp-order-volume-seg"
                  title={`${entry.storeName} ${formatCbm(entry.cbm)} m³`}
                  style={{ width: `${(entry.cbm / barBase) * 100}%`, background: SEGMENT_COLORS[index % SEGMENT_COLORS.length] }}
                />
              ))
            ) : (
              // 不能查看其他分店时没有分店明细，只显示总量
              <span className="sp-order-volume-seg" style={{ width: `${Math.min(1, ratio) * 100}%`, background: SEGMENT_COLORS[0] }} />
            )}
          </div>
        ) : null}
        {storeCbms.length ? (
          <div className="sp-order-volume-legend">
            {storeCbms.map((entry, index) => (
              <span key={entry.storeName}>
                <i className="sp-order-swatch" style={{ background: SEGMENT_COLORS[index % SEGMENT_COLORS.length] }} />
                {entry.storeName} {formatCbm(entry.cbm)}
              </span>
            ))}
          </div>
        ) : null}
      </div>
    </div>
  )
}

function Metric({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <div className="sp-order-metric">
      <div className="sp-order-metric-label">{label}</div>
      <div className="sp-order-metric-value">
        {value}
        {unit ? <small>{unit}</small> : null}
      </div>
    </div>
  )
}

function StatusDetail({ t, summary }: { t: TFunction; summary: SupplierPreorderOrderingSummary }) {
  if (summary.orderStatus === 'Submitted' && summary.submittedAtUtc) {
    return <span> · {t(`${NS}.summary.submittedAt`, { time: formatSydneyDateTime(summary.submittedAtUtc) })}</span>
  }
  if (summary.orderStatus === 'Draft' && summary.lastEditedAtUtc) {
    return (
      <span>
        {' · '}
        {t(`${NS}.summary.draftSaved`, { time: formatSydneyDateTime(summary.lastEditedAtUtc), by: summary.lastEditedBy ?? '' })}
      </span>
    )
  }
  return null
}
