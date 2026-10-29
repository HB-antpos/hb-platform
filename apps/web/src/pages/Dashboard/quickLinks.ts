import type { MenuProps } from 'antd'
import type { ReactNode } from 'react'

// 工作台「常用导航」的纯逻辑：把侧栏菜单拍平成可跳转页面、解析用户固定的页面、增删与排序。
// 存储里只放菜单 key（即路由路径），标题 / 图标每次从当前菜单解析，所以换语言、改权限、菜单改名都不会让存储过期。

export const DASHBOARD_PATH = '/dashboard'
export const QUICK_LINK_LIMIT = 12
/** 默认常用导航至少凑够的个数（有权限的默认页面不够时，用菜单里靠前的页面补）。 */
const MIN_DEFAULT_COUNT = 6

/** 没手动调整过的用户按这个顺序取有权限的页面；都是侧栏菜单里的真实路径（合并页取合并页地址）。 */
export const DEFAULT_QUICK_LINK_KEYS: readonly string[] = [
  '/pos-admin/attendance',
  '/pos-admin/payroll',
  '/warehouse/containers',
  '/pos-admin/local-supplier-invoices',
  '/system/accounts',
  '/executive-sales-intelligence/overview',
  '/pos-admin/products-pricing',
  '/pos-admin/settlement-cash',
  '/warehouse/store-orders',
]

export interface QuickLinkEntry {
  /** 菜单 key，也是路由路径。 */
  key: string
  title: string
  /** 所属一级菜单的标题与 key / 图标（本身就是一级叶子菜单时指向自己）。 */
  module: string
  moduleKey: string
  moduleIcon?: ReactNode
  /** 叶子菜单自己的图标，没有则用一级菜单图标。 */
  icon?: ReactNode
}

export interface QuickLinkModule {
  key: string
  title: string
  icon?: ReactNode
  links: QuickLinkEntry[]
}

interface MenuNode {
  key?: unknown
  label?: unknown
  icon?: ReactNode
  children?: unknown[]
}

interface ModuleOwner {
  key: string
  title: string
  icon?: ReactNode
}

/**
 * 把（已按权限裁剪的）侧栏菜单拍平成叶子页面。
 * 工作台自己不算；标题不是字符串的节点（分割线、带徽标的自定义文案）直接跳过。
 */
export function flattenMenuLinks(items: MenuProps['items']): QuickLinkEntry[] {
  const entries: QuickLinkEntry[] = []
  const seen = new Set<string>()

  const walk = (nodes: unknown[] | undefined, owner: ModuleOwner | undefined) => {
    for (const raw of nodes ?? []) {
      if (!raw || typeof raw !== 'object') continue
      const node = raw as MenuNode
      if (typeof node.key !== 'string' || typeof node.label !== 'string') continue

      if (Array.isArray(node.children) && node.children.length > 0) {
        walk(node.children, owner ?? { key: node.key, title: node.label, icon: node.icon })
        continue
      }

      if (node.key === DASHBOARD_PATH || seen.has(node.key)) continue
      seen.add(node.key)
      const moduleOwner = owner ?? { key: node.key, title: node.label, icon: node.icon }
      entries.push({
        key: node.key,
        title: node.label,
        module: moduleOwner.title,
        moduleKey: moduleOwner.key,
        moduleIcon: moduleOwner.icon,
        icon: node.icon ?? moduleOwner.icon,
      })
    }
  }

  walk(items as unknown[] | undefined, undefined)
  return entries
}

/** 按一级菜单分组，保持菜单里的出现顺序。 */
export function groupLinksByModule(entries: QuickLinkEntry[]): QuickLinkModule[] {
  const modules = new Map<string, QuickLinkModule>()
  for (const entry of entries) {
    let group = modules.get(entry.moduleKey)
    if (!group) {
      group = { key: entry.moduleKey, title: entry.module, icon: entry.moduleIcon, links: [] }
      modules.set(entry.moduleKey, group)
    }
    group.links.push(entry)
  }
  return Array.from(modules.values())
}

/** 搜索：标题或所属模块包含关键字（忽略大小写和首尾空格）。 */
export function filterLinks(entries: QuickLinkEntry[], query: string): QuickLinkEntry[] {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return entries
  return entries.filter(
    (entry) => entry.title.toLowerCase().includes(keyword) || entry.module.toLowerCase().includes(keyword),
  )
}

export function defaultPinnedKeys(entries: QuickLinkEntry[], limit = QUICK_LINK_LIMIT): string[] {
  const available = new Set(entries.map((entry) => entry.key))
  const picked = DEFAULT_QUICK_LINK_KEYS.filter((key) => available.has(key))
  for (const entry of entries) {
    if (picked.length >= MIN_DEFAULT_COUNT) break
    if (!picked.includes(entry.key)) picked.push(entry.key)
  }
  return picked.slice(0, limit)
}

/**
 * 当前应显示的固定页面：
 * - stored 为 undefined（用户从没调整过）→ 默认；
 * - 否则按存储顺序，去掉已没有权限 / 已不存在的页面和重复项（空数组表示用户主动清空）。
 */
export function resolvePinnedKeys(
  entries: QuickLinkEntry[],
  stored: readonly string[] | undefined,
  limit = QUICK_LINK_LIMIT,
): string[] {
  if (!stored) return defaultPinnedKeys(entries, limit)
  const available = new Set(entries.map((entry) => entry.key))
  const result: string[] = []
  for (const key of stored) {
    if (available.has(key) && !result.includes(key)) result.push(key)
  }
  return result.slice(0, limit)
}

/** 固定一个页面：已固定或已达上限时原样返回（同一个数组引用）。 */
export function pinKey(keys: string[], key: string, limit = QUICK_LINK_LIMIT): string[] {
  if (keys.includes(key) || keys.length >= limit) return keys
  return [...keys, key]
}

export function unpinKey(keys: string[], key: string): string[] {
  return keys.includes(key) ? keys.filter((item) => item !== key) : keys
}

/** 把 activeKey 挪到 overKey 的位置；任一不在列表里则原样返回。 */
export function moveKey(keys: string[], activeKey: string, overKey: string): string[] {
  const from = keys.indexOf(activeKey)
  const to = keys.indexOf(overKey)
  if (from < 0 || to < 0 || from === to) return keys
  const next = [...keys]
  next.splice(from, 1)
  next.splice(to, 0, activeKey)
  return next
}

export type GreetingPeriod = 'morning' | 'afternoon' | 'evening'

/** 5–11 点早上，12–17 点下午，其余晚上。 */
export function getGreetingPeriod(hour: number): GreetingPeriod {
  if (hour >= 5 && hour < 12) return 'morning'
  if (hour >= 12 && hour < 18) return 'afternoon'
  return 'evening'
}
