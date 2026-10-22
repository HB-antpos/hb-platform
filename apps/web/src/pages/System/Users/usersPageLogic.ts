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

/** 姓名拆分字段的键；顺序即表单展示顺序（姓、名、中间名、昵称）。 */
export const USER_NAME_PART_KEYS = ['familyName', 'givenName', 'middleName', 'nickname'] as const
export type UserNamePartKey = (typeof USER_NAME_PART_KEYS)[number]

/** 后端对每个姓名拆分字段的最大长度限制（字符）。 */
export const USER_NAME_PART_MAX_LENGTH = 50

type UserNamePartSource = Partial<Record<UserNamePartKey, string | null | undefined>>

/** 把详情里的姓名拆分字段转成表单初始值：null / 缺省统一显示为空串。 */
export function pickUserNamePartFormValues(user: UserNamePartSource | null | undefined): Record<UserNamePartKey, string> {
  return {
    familyName: user?.familyName ?? '',
    givenName: user?.givenName ?? '',
    middleName: user?.middleName ?? '',
    nickname: user?.nickname ?? '',
  }
}

/**
 * 计算姓、名、中间名、昵称里「有变化」的字段，用作创建 / 更新请求体的增量部分。
 *
 * 后端约定：字段 null / 不传 = 不修改，空串 = 清空，非空 = 设置；并且只要请求里任何一个
 * 姓 / 名 / 中间名 非 null，就会用有效三段重算 FullName。所以：
 * - 与加载时的原值（original）相同的字段一律不返回，避免把旧数据误写回去或无谓触发 FullName 重算；
 * - 变成空的字段返回 ''（表示清空），而不是 undefined；
 * - 比较时忽略首尾空白，null / 缺省与空串视为同一种「未填写」；
 * - 创建用户时 original 传 undefined，结果即「用户实际填写了的字段」。
 */
export function diffUserNameParts(
  original: UserNamePartSource | null | undefined,
  current: UserNamePartSource | null | undefined,
): Partial<Record<UserNamePartKey, string>> {
  const changes: Partial<Record<UserNamePartKey, string>> = {}
  for (const key of USER_NAME_PART_KEYS) {
    const before = original?.[key]?.trim() ?? ''
    const after = current?.[key]?.trim() ?? ''
    if (before !== after) changes[key] = after
  }
  return changes
}

// ---------------------------------------------------------------------------
// 「允许登录」开关（管理员可开关任意账号的登录权限；与「启用 / 停用账号」是两回事）
// ---------------------------------------------------------------------------

/**
 * 账号是否被限制登录。接口 JSON 会省略 null，所以只有后端明确返回 false 才算「被限制」；
 * 缺省（旧后端 / 未查询该字段）既不能当作「允许」，也不能当作「限制」，界面不显示任何标记。
 */
export function isLoginRestricted(user: { loginEnabled?: boolean | null } | null | undefined): boolean {
  return user?.loginEnabled === false
}

export interface LoginToggleState {
  /** 是否渲染开关：需要编辑权限，且后端返回了 loginEnabled（否则不知道当前状态，不能显示开关）。 */
  visible: boolean
  /** 当前是否允许登录；visible 为 false 时无意义。 */
  checked: boolean
  /** 是否置灰：本人账号不能关闭自己的登录权限（后端同样拒绝，否则操作者会把自己锁在系统外）。 */
  disabled: boolean
  disabledReason?: 'self'
  /** 点击后要提交的目标值（与当前状态相反）。 */
  nextEnabled: boolean
}

export function getLoginToggleState({
  canEdit,
  loginEnabled,
  isSelf,
}: {
  canEdit: boolean
  loginEnabled: boolean | null | undefined
  isSelf: boolean
}): LoginToggleState {
  const known = typeof loginEnabled === 'boolean'
  const checked = loginEnabled !== false
  return {
    visible: canEdit && known,
    checked,
    disabled: isSelf,
    disabledReason: isSelf ? 'self' : undefined,
    nextEnabled: !checked,
  }
}

/**
 * 把「允许登录」的新值写回某一行 / 详情对象：只改 guid 匹配的那一个，其余原样返回（含 null）。
 * 用于开关成功后立刻更新列表、抽屉标题与资料页，再由列表重新加载拿服务端真值。
 */
export function withLoginEnabled<T extends { userGUID: string; loginEnabled?: boolean }>(
  user: T | null,
  userGuid: string,
  loginEnabled: boolean,
): T | null {
  if (!user || user.userGUID !== userGuid) return user
  return { ...user, loginEnabled }
}

/**
 * 开启登录后是否要提示「可能还没有可用密码」：从没登录过（lastLoginAt 为空 / 无法解析）的账号，
 * 多半是创建时没打开「允许登录」，也就没设过可用密码。关闭登录不需要这个提示。
 */
export function planLoginToggleNotice(
  nextEnabled: boolean,
  lastLoginAt: string | null | undefined,
): { result: 'allowed' | 'restricted'; showPasswordHint: boolean } {
  return {
    result: nextEnabled ? 'allowed' : 'restricted',
    showPasswordHint: nextEnabled && !parseUserUtcTimestamp(lastLoginAt),
  }
}

export type LoginToggleErrorKind =
  | 'self'
  | 'notFound'
  | 'scopeDenied'
  | 'highPrivilege'
  | 'forbidden'
  | 'generic'

/** 每类错误对应的页面文案键与中文兜底；只展示这里的文案，不透传后端原始 message。 */
export const LOGIN_TOGGLE_ERROR_MESSAGES: Record<LoginToggleErrorKind, { key: string; fallback: string }> = {
  self: { key: 'system.usersWorkspace.loginToggleError.self', fallback: '不能关闭本人账号的登录权限' },
  notFound: { key: 'system.usersWorkspace.loginToggleError.notFound', fallback: '该用户不存在或已被删除，请刷新列表' },
  scopeDenied: { key: 'system.usersWorkspace.loginToggleError.scopeDenied', fallback: '该用户不在你的管理范围内' },
  highPrivilege: { key: 'system.usersWorkspace.loginToggleError.highPrivilege', fallback: '无权修改高权限账号的登录权限' },
  forbidden: { key: 'system.usersWorkspace.loginToggleError.forbidden', fallback: '当前账号没有修改登录权限的权限' },
  generic: { key: 'system.usersWorkspace.loginToggleError.generic', fallback: '修改登录权限失败，请稍后重试' },
}

const LOGIN_TOGGLE_ERROR_CODES: Record<string, LoginToggleErrorKind> = {
  SELF_PROFILE_FIELDS_DENIED: 'self',
  USER_NOT_FOUND: 'notFound',
  USER_SCOPE_DENIED: 'scopeDenied',
  HIGH_PRIVILEGE_TARGET_DENIED: 'highPrivilege',
}

/**
 * 把 updateUserLoginEnabled 抛出的错误归类：优先按后端 errorCode（payload.errorCode / code，大小写不敏感），
 * 没有业务码时 HTTP 403 视为无 Users.Edit 权限，其余一律通用失败。
 */
export function resolveLoginToggleErrorKind(error: unknown): LoginToggleErrorKind {
  const record = error && typeof error === 'object' ? (error as { payload?: unknown; status?: unknown }) : undefined
  const payload = record?.payload && typeof record.payload === 'object'
    ? (record.payload as { errorCode?: unknown; code?: unknown })
    : undefined
  const rawCode = payload?.errorCode ?? payload?.code
  const code = typeof rawCode === 'string' ? rawCode.trim().toUpperCase() : ''
  const byCode = code ? LOGIN_TOGGLE_ERROR_CODES[code] : undefined
  if (byCode) return byCode
  return record?.status === 403 ? 'forbidden' : 'generic'
}

/**
 * 新建用户：昵称默认跟「名」一致（昵称会印在小票和员工码上）。
 * 昵称为空、或仍等于改动前的名（没被单独改过）时返回新名作为昵称；否则返回 null 表示不改。
 */
export function syncCreateNicknameWithGivenName(
  previousGivenName: string | undefined,
  nextGivenName: string | undefined,
  currentNickname: string | undefined,
): string | null {
  const nickname = currentNickname ?? ''
  if (nickname.trim() === '' || nickname === (previousGivenName ?? '')) {
    return nextGivenName ?? ''
  }
  return null
}
