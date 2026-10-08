import dayjs from 'dayjs'
import type { Key } from 'react'

import type { BatchUserStoreOperationResult, UserDto } from '../../../types/user'

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

/** 一次批量操作最多勾选的用户数：后端启停用在单个可串行化事务里逐个校验，过多会长时间持锁。 */
export const USER_BATCH_SELECTION_LIMIT = 100

/**
 * 合并跨页勾选：antd 在 preserveSelectedRowKeys 下只回传当前页可见的行对象，
 * 其他页已勾选的行需要从上一次的选择里补回，才能继续按角色、状态做批量判断。
 */
export function mergeSelectedUsers(
  previous: UserDto[],
  selectedKeys: readonly Key[],
  visibleRows: readonly (UserDto | undefined)[],
): UserDto[] {
  const byGuid = new Map(previous.map((user) => [user.userGUID, user]))
  for (const row of visibleRows) {
    if (row) byGuid.set(row.userGUID, row)
  }
  return selectedKeys
    .map((key) => byGuid.get(String(key)))
    .filter((user): user is UserDto => !!user)
}

export interface BatchStatusPlan {
  targets: UserDto[]
  /** 当前登录账号：不允许批量停用 / 启用自己，避免把自己锁在系统外（后端同样拒绝）。 */
  skippedSelf: UserDto[]
  /** 已经处于目标状态的账号，无需提交。 */
  skippedUnchanged: UserDto[]
  /** 范围受限的店长无权编辑的账号（例如持有高权限角色）。 */
  skippedOutOfScope: UserDto[]
}

export function planBatchStatusChange(
  users: readonly UserDto[],
  nextActive: boolean,
  currentUserGuid: string | undefined,
  isOutOfScope: (user: UserDto) => boolean = () => false,
): BatchStatusPlan {
  const plan: BatchStatusPlan = { targets: [], skippedSelf: [], skippedUnchanged: [], skippedOutOfScope: [] }
  for (const user of users) {
    if (currentUserGuid && user.userGUID === currentUserGuid) plan.skippedSelf.push(user)
    else if (user.isActive === nextActive) plan.skippedUnchanged.push(user)
    else if (isOutOfScope(user)) plan.skippedOutOfScope.push(user)
    else plan.targets.push(user)
  }
  return plan
}

/** 与后端 Permissions.StoreManagerRoleNames 保持一致（大小写不敏感）。 */
const STORE_MANAGER_ROLE_NAMES = ['storemanager', '店长', '经理']

/** 店长角色由「可管理分店」自动派生，后端拒绝直接在角色上增删成员，批量角色操作不提供它。 */
export function isDerivedStoreManagerRoleName(roleName: string | null | undefined): boolean {
  const normalized = roleName?.trim().toLowerCase()
  return !!normalized && STORE_MANAGER_ROLE_NAMES.includes(normalized)
}

export type BatchRoleMode = 'add' | 'remove'

export interface BatchRolePlan {
  targets: UserDto[]
  /** 添加时已持有该角色、移除时本就没有该角色的账号。 */
  skipped: UserDto[]
}

/** 按列表行上的角色名判断是否已持有角色；角色名比较大小写不敏感，与后端一致。 */
export function planBatchRoleChange(users: readonly UserDto[], roleName: string, mode: BatchRoleMode): BatchRolePlan {
  const normalized = roleName.trim().toLowerCase()
  const plan: BatchRolePlan = { targets: [], skipped: [] }
  for (const user of users) {
    const hasRole = (user.roleNames ?? []).some((name) => name.trim().toLowerCase() === normalized)
    if (hasRole === (mode === 'add')) plan.skipped.push(user)
    else plan.targets.push(user)
  }
  return plan
}

/** 取服务端业务消息（RequestError.payload.message），没有时退回通用文案。 */
export function getServerErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'payload' in error) {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object' && 'message' in payload) {
      const text = (payload as { message?: unknown }).message
      if (typeof text === 'string' && text.trim()) return text
    }
  }
  return fallback
}

/** 批量分店操作实际会写入的关联数；为 0 时没有可提交的变更。 */
export function countBatchStoreChanges(result: BatchUserStoreOperationResult | null | undefined): number {
  if (!result) return 0
  return result.addedCount + result.upgradedCount + result.removedCount
}
