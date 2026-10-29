import type { TabItem } from '../types/router'

// 页签的纯逻辑：锁定、分组排序、批量关闭范围。
// 页签条的顺序约定为「固定页签（工作台）→ 已锁定页签 → 普通页签」，三段各自连续。

/** 已锁定页签：固定页签（工作台）本身不可关闭，不参与锁定。 */
export function isLockedTab(tab: Pick<TabItem, 'affix' | 'locked'>): boolean {
  return !tab.affix && tab.locked === true
}

/** 能被「×」或批量操作关闭：不是固定页签、没锁定、也没被路由声明为不可关闭。 */
export function isClosableTab(tab: TabItem): boolean {
  return !tab.affix && !isLockedTab(tab) && tab.closable !== false
}

/** 按「固定 → 锁定 → 普通」稳定分段；同段内保持原有相对顺序，同时清掉固定页签上不该有的 locked。 */
export function normalizeTabOrder(tabs: TabItem[]): TabItem[] {
  const affix: TabItem[] = []
  const locked: TabItem[] = []
  const normal: TabItem[] = []
  for (const tab of tabs) {
    if (tab.affix) {
      affix.push(tab.locked ? { ...tab, locked: undefined } : tab)
    } else if (tab.locked) {
      locked.push(tab)
    } else {
      normal.push(tab)
    }
  }
  return [...affix, ...locked, ...normal]
}

/**
 * 锁定 / 取消锁定。
 * - 锁定：排到锁定段末尾（紧跟已锁定页签）；
 * - 取消锁定：排到普通段开头（紧跟锁定段），不会跳到最右边。
 * 两者都靠「改标记 + 稳定分段」实现。固定页签、不存在的 key 原样返回。
 */
export function setTabLocked(tabs: TabItem[], key: string, locked: boolean): TabItem[] {
  const target = tabs.find((item) => item.key === key)
  if (!target || target.affix || isLockedTab(target) === locked) {
    return tabs
  }
  return normalizeTabOrder(
    tabs.map((item) => (item.key === key ? { ...item, locked: locked ? true : undefined } : item)),
  )
}

/** 同一分组（都锁定或都不锁定）内才能互换位置；固定页签不参与。返回新数组，不合法时返回原数组。 */
export function moveTabWithinGroup(tabs: TabItem[], activeKey: string, overKey: string): TabItem[] {
  if (activeKey === overKey) {
    return tabs
  }
  const active = tabs.find((item) => item.key === activeKey)
  const over = tabs.find((item) => item.key === overKey)
  if (!active || !over || active.affix || over.affix || isLockedTab(active) !== isLockedTab(over)) {
    return tabs
  }

  const activeIndex = tabs.indexOf(active)
  const overIndex = tabs.indexOf(over)
  const next = [...tabs]
  next.splice(activeIndex, 1)
  next.splice(overIndex, 0, active)
  return next
}

/** 同组内左 / 右相邻页签的 key，没有则返回 undefined（键盘 Alt+←/→ 调整顺序用）。 */
export function getAdjacentTabKey(tabs: TabItem[], key: string, direction: 'left' | 'right'): string | undefined {
  const index = tabs.findIndex((item) => item.key === key)
  const current = tabs[index]
  if (!current || current.affix) {
    return undefined
  }
  const neighbor = tabs[direction === 'left' ? index - 1 : index + 1]
  if (!neighbor || neighbor.affix || isLockedTab(neighbor) !== isLockedTab(current)) {
    return undefined
  }
  return neighbor.key
}

export type CloseScope = 'others' | 'left' | 'right'

/**
 * 批量关闭会关掉哪些页签：只取「可关闭」的（跳过固定、锁定），目标页签自己永远保留。
 * 左 / 右按页签条中的位置算。
 */
export function getClosableKeys(tabs: TabItem[], key: string, scope: CloseScope): string[] {
  const index = tabs.findIndex((item) => item.key === key)
  if (index < 0) {
    return []
  }
  return tabs
    .filter((item, itemIndex) => {
      if (itemIndex === index || !isClosableTab(item)) {
        return false
      }
      if (scope === 'left') return itemIndex < index
      if (scope === 'right') return itemIndex > index
      return true
    })
    .map((item) => item.key)
}
