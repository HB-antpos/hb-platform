// 管理端几个页面共用的小组件：订单表状态、分店下单状态、商品缩略图、截止提示。
import { Image, Tag } from 'antd'
import { useTranslation } from 'react-i18next'

import StatusPill, { type StatusPillTone } from '../../../components/listToolbar/StatusPill'
import type {
  SupplierPreorderFormStatus,
  SupplierPreorderPreviewStatus,
  SupplierPreorderStoreOrderStatus,
} from '../../../types/supplierPreorder'

import { getDeadlineInfo, formatSydneyDateTime } from './supplierPreorderAdmin.logic'

const FORM_STATUS_TONE: Record<SupplierPreorderFormStatus, StatusPillTone> = {
  Draft: 'orange',
  Open: 'blue',
  Closed: 'gray',
}

export function FormStatusPill({ status }: { status: SupplierPreorderFormStatus }) {
  const { t } = useTranslation()
  return <StatusPill tone={FORM_STATUS_TONE[status] ?? 'gray'}>{t(`spAdmin.common.status.${status}`)}</StatusPill>
}

const ORDER_STATUS_COLOR: Record<SupplierPreorderStoreOrderStatus, string> = {
  None: 'default',
  Draft: 'blue',
  Submitted: 'green',
}

export function OrderStatusTag({ status }: { status: SupplierPreorderStoreOrderStatus }) {
  const { t } = useTranslation()
  return (
    <Tag color={ORDER_STATUS_COLOR[status] ?? 'default'} bordered={status !== 'None'} className="sp-admin-tag">
      {t(`spAdmin.common.orderStatus.${status}`)}
    </Tag>
  )
}

const PREVIEW_STATUS_COLOR: Record<SupplierPreorderPreviewStatus, string> = {
  Matched: 'green',
  New: 'gold',
  Warning: 'orange',
  Invalid: 'red',
}

export function PreviewStatusTag({ status, suffix }: { status: SupplierPreorderPreviewStatus; suffix?: string | null }) {
  const { t } = useTranslation()
  return (
    <Tag color={PREVIEW_STATUS_COLOR[status] ?? 'default'} className="sp-admin-tag">
      {t(`spAdmin.common.previewStatus.${status}`)}
      {suffix ? ` · ${suffix}` : ''}
    </Tag>
  )
}

/** 36px 商品缩略图：懒加载，点开看大图；没有图片时显示空白占位。 */
export function ItemThumb({ url, size = 36 }: { url?: string | null; size?: number }) {
  if (!url) return <span className="sp-admin-thumb-empty" style={{ width: size, height: size }} aria-hidden="true" />
  return (
    <Image
      src={url}
      width={size}
      height={size}
      alt=""
      loading="lazy"
      className="sp-admin-thumb"
      style={{ objectFit: 'cover' }}
      preview={{ mask: null }}
    />
  )
}

/** 截止时间（悉尼）+ 剩余天数；剩 ≤3 天琥珀色。 */
export function DeadlineText({
  deadlineAtUtc,
  nowMs,
  showDate = true,
  showRemaining = true,
}: {
  deadlineAtUtc?: string | null
  nowMs: number
  showDate?: boolean
  /** 已截止的订单表只显示日期，不再显示剩余天数 */
  showRemaining?: boolean
}) {
  const { t } = useTranslation()
  const info = getDeadlineInfo(deadlineAtUtc, nowMs)
  if (info.state === 'none') return <span className="sp-admin-muted">{t('spAdmin.common.deadline.none')}</span>
  if (!showRemaining) return <span className="sp-admin-number">{formatSydneyDateTime(deadlineAtUtc)}</span>
  const brief =
    info.state === 'passed'
      ? t('spAdmin.common.deadline.passed')
      : info.state === 'today'
        ? t('spAdmin.common.deadline.today')
        : t('spAdmin.common.deadline.days', { count: info.daysLeft ?? 0 })
  return (
    <span className="sp-admin-deadline">
      {showDate ? <span className="sp-admin-number">{formatSydneyDateTime(deadlineAtUtc)}</span> : null}
      <span className={info.urgent ? 'sp-admin-urgent' : 'sp-admin-muted'}>{brief}</span>
    </span>
  )
}
