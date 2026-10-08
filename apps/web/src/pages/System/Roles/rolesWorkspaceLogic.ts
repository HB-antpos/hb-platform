import type { PermissionAliasDto, PermissionCategoryDto, RoleDto } from '../../../types/role'
import type { WebMenuPreviewNode } from '../../../utils/webMenuPreview'
import { sortRolePermissionCategories } from './rolePermissionCategories'

/**
 * 与后端 Permissions.ExpandPermissionCodes 保持一致：
 * 持有 aliasCodes 中任一权限码即自动获得 canonicalCode（大小写不敏感，反向不成立）。
 * 用于在未保存的草稿上推导菜单可见性。
 */
export function expandRolePermissionCodes(codes: string[], aliases: PermissionAliasDto[]): string[] {
  const result: string[] = []
  const seen = new Set<string>()
  const add = (code: string) => {
    const normalized = code.toLowerCase()
    if (seen.has(normalized)) return
    seen.add(normalized)
    result.push(code)
  }

  const held = new Set(codes.filter((code) => code.trim()).map((code) => code.toLowerCase()))
  for (const code of codes) {
    if (code.trim()) add(code)
  }
  for (const alias of aliases) {
    if (alias.aliasCodes.some((aliasCode) => held.has(aliasCode.toLowerCase()))) {
      add(alias.canonicalCode)
    }
  }
  return result
}

export function diffPermissionCodes(baseline: string[], draft: string[]) {
  const baselineSet = new Set(baseline)
  const draftSet = new Set(draft)
  return {
    added: draft.filter((code) => !baselineSet.has(code)),
    removed: baseline.filter((code) => !draftSet.has(code)),
  }
}

/** 角色列表本地检索：角色数量很少，按名称与描述即时过滤，免去等待请求。 */
export function filterRoles(roles: RoleDto[], keyword: string): RoleDto[] {
  const normalized = keyword.trim().toLowerCase()
  if (!normalized) return roles
  return roles.filter((role) =>
    role.roleName.toLowerCase().includes(normalized) ||
    (role.description ?? '').toLowerCase().includes(normalized),
  )
}

export type PermissionFilter = 'all' | 'granted' | 'ungranted' | 'changed'

export interface PermissionItemView {
  code: string
  displayName: string
  description?: string
  granted: boolean
  change: 'added' | 'removed' | null
}

export interface PermissionGroupView {
  key: string
  displayName: string
  /** 分组内全部权限数（不受搜索/筛选影响），用于表头「已授予 / 总数」。 */
  total: number
  granted: number
  codes: string[]
  items: PermissionItemView[]
}

/**
 * 按模块分组构建权限清单视图。
 * 搜索与「已授予/未授予/已修改」筛选只影响显示的条目；分组统计始终按整组计算，
 * 分段控件上的数量按当前搜索结果计算，与列表保持一致。
 */
export function buildPermissionGroupViews({
  categories,
  draftCodes,
  baselineCodes,
  keyword,
  filter,
}: {
  categories: PermissionCategoryDto[]
  draftCodes: string[]
  baselineCodes: string[]
  keyword: string
  filter: PermissionFilter
}) {
  const draftSet = new Set(draftCodes)
  const baselineSet = new Set(baselineCodes)
  const normalizedKeyword = keyword.trim().toLowerCase()
  const counts: Record<PermissionFilter, number> = { all: 0, granted: 0, ungranted: 0, changed: 0 }
  const groups: PermissionGroupView[] = []

  for (const category of sortRolePermissionCategories(categories)) {
    const allItems = category.permissions.map<PermissionItemView>((permission) => {
      const granted = draftSet.has(permission.name)
      const wasGranted = baselineSet.has(permission.name)
      return {
        code: permission.name,
        displayName: permission.displayName || permission.name,
        description: permission.description,
        granted,
        change: granted === wasGranted ? null : granted ? 'added' : 'removed',
      }
    })

    const searchMatched = normalizedKeyword
      ? allItems.filter((item) =>
        item.code.toLowerCase().includes(normalizedKeyword) ||
        item.displayName.toLowerCase().includes(normalizedKeyword),
      )
      : allItems

    for (const item of searchMatched) {
      counts.all += 1
      if (item.granted) counts.granted += 1
      else counts.ungranted += 1
      if (item.change) counts.changed += 1
    }

    const items = searchMatched.filter((item) => {
      if (filter === 'granted') return item.granted
      if (filter === 'ungranted') return !item.granted
      if (filter === 'changed') return item.change !== null
      return true
    })
    if (!items.length) continue

    groups.push({
      key: category.category,
      displayName: category.displayName || category.category,
      total: allItems.length,
      granted: allItems.filter((item) => item.granted).length,
      codes: allItems.map((item) => item.code),
      items,
    })
  }

  return { groups, counts }
}

/** 展平菜单树为 key → 是否可见，用于对比草稿与已保存状态的可见性差异。 */
export function collectWebMenuVisibility(nodes: WebMenuPreviewNode[], target = new Map<string, boolean>()) {
  for (const node of nodes) {
    target.set(node.key, node.visible)
    if (node.children?.length) {
      collectWebMenuVisibility(node.children, target)
    }
  }
  return target
}

/** 与后端 Permissions.StoreManagerRoleNames 保持一致（大小写不敏感）。 */
const STORE_MANAGER_ROLE_NAMES = ['storemanager', '店长', '经理']

/**
 * 店长角色是派生角色：后端按用户是否持有「可管理分店」自动同步，
 * 直接在角色上添加 / 移除成员会被拒绝（DERIVED_STORE_MANAGER_ROLE）。
 */
export function isDerivedStoreManagerRole(roleName: string | null | undefined): boolean {
  const normalized = roleName?.trim().toLowerCase()
  return !!normalized && STORE_MANAGER_ROLE_NAMES.includes(normalized)
}

/** 取服务端业务消息（RequestError.payload.message），没有时退回通用文案。 */
export function getRoleMutationErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'payload' in error) {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object' && 'message' in payload) {
      const message = (payload as { message?: unknown }).message
      if (typeof message === 'string' && message.trim()) return message
    }
  }
  return fallback
}
