// 下单列表页与下单页共用的小组件：截止时间、订单表状态、分店订单状态。
import { Tag } from 'antd'
import type { TFunction } from 'i18next'

import type {
  SupplierPreorderFormStatus,
  SupplierPreorderHolidayType,
  SupplierPreorderStoreOrderStatus,
} from '../../../types/supplierPreorder'

import { formatSydneyDateTime, type DeadlineState } from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

/** 节日 · 年份，例如「圣诞 2026」 */
export function formatHolidayYear(t: TFunction, holidayType: SupplierPreorderHolidayType, seasonYear: number): string {
  return `${t(`${NS}.holiday.${holidayType}`, { defaultValue: holidayType })} ${seasonYear}`
}

/** 剩余时间文案：剩 N 天 / 剩 N 小时 / 已截止；无截止返回空串 */
export function formatRemaining(t: TFunction, state: DeadlineState): string {
  if (state.kind === 'none') return ''
  if (state.kind === 'closed') return t(`${NS}.deadline.closed`)
  if (state.hoursLeft !== null) return t(`${NS}.deadline.remainHours`, { count: state.hoursLeft })
  return t(`${NS}.deadline.remainDays`, { count: state.daysLeft ?? 0 })
}

export function deadlineClassName(state: DeadlineState): string {
  if (state.kind === 'urgent') return 'sp-order-deadline is-urgent'
  if (state.kind === 'closed') return 'sp-order-deadline is-closed'
  return 'sp-order-deadline'
}

/** 截止时间（悉尼）+ 剩余；已截止时整体灰色 */
export function DeadlineText({ t, deadlineAtUtc, state }: { t: TFunction; deadlineAtUtc?: string | null; state: DeadlineState }) {
  if (!deadlineAtUtc && state.kind !== 'closed') return null
  const remaining = formatRemaining(t, state)
  return (
    <span className={deadlineClassName(state)}>
      {deadlineAtUtc ? formatSydneyDateTime(deadlineAtUtc) : null}
      {deadlineAtUtc && remaining ? ' · ' : null}
      {remaining}
    </span>
  )
}

const FORM_STATUS_COLOR: Record<SupplierPreorderFormStatus, string> = {
  Draft: 'default',
  Open: 'processing',
  Closed: 'default',
}

export function FormStatusTag({ t, status, isLocked }: { t: TFunction; status: SupplierPreorderFormStatus; isLocked: boolean }) {
  // 对普通下单人已锁定（过了截止）时统一显示「已截止」
  if (isLocked && status === 'Open') return <Tag>{t(`${NS}.formStatus.Locked`)}</Tag>
  return <Tag color={FORM_STATUS_COLOR[status]}>{t(`${NS}.formStatus.${status}`, { defaultValue: status })}</Tag>
}

const STORE_STATUS_COLOR: Record<SupplierPreorderStoreOrderStatus, string> = {
  None: 'default',
  Draft: 'gold',
  Submitted: 'green',
}

export function StoreStatusTag({ t, status }: { t: TFunction; status: SupplierPreorderStoreOrderStatus }) {
  return <Tag color={STORE_STATUS_COLOR[status]}>{t(`${NS}.storeStatus.${status}`, { defaultValue: status })}</Tag>
}
