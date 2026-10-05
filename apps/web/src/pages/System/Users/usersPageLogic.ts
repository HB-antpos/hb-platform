import dayjs from 'dayjs'
import { parseUserUtcTimestamp } from './time'

export type UserStatusFilter = 'all' | 'active' | 'inactive'

/** 列表状态分段 → 后端 isActive 查询参数；「全部」不传参数。 */
export function toIsActiveQuery(filter: UserStatusFilter): boolean | undefined {
  if (filter === 'active') return true
  if (filter === 'inactive') return false
  return undefined
}

/** 超过该天数未登录的账号在列表中弱提示。 */
export const STALE_LOGIN_DAYS = 30

export type LastLoginDescription =
  | { kind: 'never' }
  | { kind: 'justNow' }
  | { kind: 'minutes'; count: number }
  | { kind: 'hours'; count: number }
  | { kind: 'yesterday' }
  | { kind: 'days'; count: number; stale: boolean }

/**
 * 把最近登录时间描述为相对时间：一天内按分钟/小时，跨天后按本地自然日计算，
 * 便于在列表里快速扫出长期未登录的账号。
 */
export function describeLastLogin(value: string | null | undefined, now: Date): LastLoginDescription {
  const parsed = parseUserUtcTimestamp(value)
  if (!parsed) {
    return { kind: 'never' }
  }

  const current = dayjs(now)
  const elapsedMinutes = Math.max(0, current.diff(parsed, 'minute'))
  if (elapsedMinutes < 1) {
    return { kind: 'justNow' }
  }
  if (elapsedMinutes < 60) {
    return { kind: 'minutes', count: elapsedMinutes }
  }
  if (elapsedMinutes < 24 * 60) {
    return { kind: 'hours', count: Math.floor(elapsedMinutes / 60) }
  }

  const calendarDays = Math.max(1, current.startOf('day').diff(parsed.startOf('day'), 'day'))
  if (calendarDays === 1) {
    return { kind: 'yesterday' }
  }
  return { kind: 'days', count: calendarDays, stale: calendarDays >= STALE_LOGIN_DAYS }
}

interface UserIdentity {
  username: string
  email?: string | null
  fullName?: string | null
}

export function getUserDisplayName(user: UserIdentity): string {
  const fullName = user.fullName?.trim()
  return fullName || user.username
}

/** 第二行：姓名与用户名不同时补上 @用户名，避免同名用户难以区分。 */
export function getUserSecondaryLine(user: UserIdentity): string {
  const displayName = getUserDisplayName(user)
  const email = user.email?.trim() ?? ''
  if (displayName === user.username) {
    return email
  }
  return email ? `@${user.username} · ${email}` : `@${user.username}`
}

export function getUserInitial(name: string): string {
  const first = Array.from(name.trim())[0]
  return first ? first.toUpperCase() : '?'
}

/** 分店标签只显示前几家，其余折叠为 +N；排序与原列表一致按名称。 */
export function splitVisibleStores(storeNames: string[] | undefined, maxVisible = 2) {
  const sorted = [...(storeNames ?? [])].sort((left, right) => left.localeCompare(right))
  return {
    visible: sorted.slice(0, maxVisible),
    hidden: sorted.slice(maxVisible),
  }
}

export function diffAssignmentKeys(baseline: string[], draft: string[]) {
  const baselineSet = new Set(baseline)
  const draftSet = new Set(draft)
  return {
    added: draft.filter((key) => !baselineSet.has(key)),
    removed: baseline.filter((key) => !draftSet.has(key)),
  }
}

/**
 * 分店草稿差异：新增/移除的分店，以及前后都关联但「可管理」开关变化的分店。
 * 新增分店的可管理状态随新增一起保存，不重复计数。
 */
export function diffStoreAssignment({
  baselineStores,
  draftStores,
  baselineManageable,
  draftManageable,
}: {
  baselineStores: string[]
  draftStores: string[]
  baselineManageable: string[]
  draftManageable: string[]
}) {
  const { added, removed } = diffAssignmentKeys(baselineStores, draftStores)
  const baselineStoreSet = new Set(baselineStores)
  const baselineManageableSet = new Set(baselineManageable)
  const draftManageableSet = new Set(draftManageable)
  const manageableChanged = draftStores.filter((storeGuid) =>
    baselineStoreSet.has(storeGuid) &&
    baselineManageableSet.has(storeGuid) !== draftManageableSet.has(storeGuid),
  )
  return { added, removed, manageableChanged }
}
