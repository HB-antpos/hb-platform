import type { AccessControl } from '../types/auth'
import type { NavGroupDefinition, NavGroupTab } from '../types/router'

/** 当前账号能看到的标签（逐个按原页面的 accessKey 过滤，顺序不变）。 */
export function getVisibleNavGroupTabs(group: NavGroupDefinition, access: AccessControl): NavGroupTab[] {
  return group.tabs.filter((tab) => access[tab.accessKey] === true)
}

/** 当前地址对应的标签；地址不属于本组（合并页被缓存在后台）时返回 undefined。 */
export function matchNavGroupTab(tabs: NavGroupTab[], pathname: string): NavGroupTab | undefined {
  const normalized = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  return tabs.find((tab) => tab.path === normalized)
}

/**
 * 标签内容是否挂载：
 * - 当前标签一定挂载；
 * - 不保活的标签（含敏感数据）只在「本组正在前台且它是当前标签」时挂载，切走或离开合并页即卸载；
 * - 保活标签访问过一次后一直挂载，切回来保留筛选与分页。
 */
export function shouldMountNavGroupTab(
  tab: NavGroupTab,
  options: { activePath: string | undefined; groupInForeground: boolean; visited: ReadonlySet<string> },
): boolean {
  const isActive = options.groupInForeground && tab.path === options.activePath
  if (isActive) {
    return true
  }
  if (!tab.keepAlive) {
    return false
  }
  return options.visited.has(tab.path)
}

/** 合并页的任一标签有权限即可见。 */
export function getNavGroupAccessKeys(group: NavGroupDefinition): (keyof AccessControl)[] {
  return Array.from(new Set(group.tabs.map((tab) => tab.accessKey)))
}
