import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { TabItem } from '../types/router'
import { getAffixTabs, normalizePersistedTabs, resolveRoute } from '../router/routes'
import {
  type CloseScope,
  getClosableKeys,
  isLockedTab,
  moveTabWithinGroup,
  normalizeTabOrder,
  setTabLocked,
} from './tabsLogic'

interface TabsState {
  activeKey: string
  tabs: TabItem[]
  pinTabsBar: boolean
  setActiveKey: (key: string) => void
  setPinTabsBar: (value: boolean) => void
  ensureTab: (tab: TabItem) => void
  updateTabTitle: (key: string, title: string) => void
  moveTab: (activeKey: string, overKey: string) => void
  lockTab: (key: string) => void
  unlockTab: (key: string) => void
  removeTab: (key: string) => string
  removeOtherTabs: (key: string) => string[]
  removeLeftTabs: (key: string) => string[]
  removeRightTabs: (key: string) => string[]
  resetTabs: () => void
}

const defaultTabs = getAffixTabs()
const defaultActiveKey = defaultTabs[0]?.key ?? '/dashboard'

// 批量关闭统一走 getClosableKeys：固定页签与已锁定页签永远跳过，目标页签保留并成为当前页签。
function removeTabsByScope(
  get: () => TabsState,
  set: (partial: Partial<TabsState>) => void,
  key: string,
  scope: CloseScope,
): string[] {
  const currentTabs = get().tabs
  if (!currentTabs.some((item) => item.key === key)) {
    return []
  }

  const removedKeys = getClosableKeys(currentTabs, key, scope)
  const removedSet = new Set(removedKeys)
  set({
    tabs: currentTabs.filter((item) => !removedSet.has(item.key)),
    activeKey: key,
  })

  return removedKeys
}

export const useTabsStore = create<TabsState>()(
  persist(
    (set, get) => ({
      activeKey: defaultActiveKey,
      tabs: defaultTabs,
      pinTabsBar: true,
      setActiveKey: (key) => set({ activeKey: key }),
      setPinTabsBar: (value) => set({ pinTabsBar: value }),
      ensureTab: (tab) => {
        const existing = get().tabs.find((item) => item.key === tab.key)
        if (existing) {
          // 合并页同组标签共用一个页签：切换标签时更新页签记住的地址，点页签能回到当前标签。
          set({
            activeKey: tab.key,
            tabs: existing.path === tab.path
              ? get().tabs
              : get().tabs.map((item) => (item.key === tab.key ? { ...item, path: tab.path } : item)),
          })
          return
        }

        set({
          tabs: [...get().tabs, tab],
          activeKey: tab.key,
        })
      },
      updateTabTitle: (key, title) => {
        set({
          tabs: get().tabs.map((item) => (item.key === key ? { ...item, title } : item)),
        })
      },
      moveTab: (activeKey, overKey) => {
        // 锁定组与普通组各自排序，跨组的拖动在 moveTabWithinGroup 里被忽略。
        const nextTabs = moveTabWithinGroup(get().tabs, activeKey, overKey)
        if (nextTabs !== get().tabs) {
          set({ tabs: nextTabs })
        }
      },
      lockTab: (key) => set({ tabs: setTabLocked(get().tabs, key, true) }),
      unlockTab: (key) => set({ tabs: setTabLocked(get().tabs, key, false) }),
      removeTab: (key) => {
        const currentTabs = get().tabs
        const target = currentTabs.find((item) => item.key === key)

        // 固定页签与已锁定页签不能被关闭。
        if (!target || target.affix || isLockedTab(target)) {
          return get().activeKey
        }

        const nextTabs = currentTabs.filter((item) => item.key !== key)
        const removedIndex = currentTabs.findIndex((item) => item.key === key)
        const nextActiveKey =
          get().activeKey === key
            ? nextTabs[removedIndex]?.key || nextTabs[removedIndex - 1]?.key || defaultActiveKey
            : get().activeKey

        set({
          tabs: nextTabs.length > 0 ? nextTabs : defaultTabs,
          activeKey: nextActiveKey,
        })

        return nextActiveKey
      },
      removeOtherTabs: (key) => removeTabsByScope(get, set, key, 'others'),
      removeLeftTabs: (key) => removeTabsByScope(get, set, key, 'left'),
      removeRightTabs: (key) => removeTabsByScope(get, set, key, 'right'),
      resetTabs: () =>
        set({
          tabs: defaultTabs,
          activeKey: defaultActiveKey,
        }),
    }),
    {
      name: 'react-vite-admin-tabs-v2',
      // 读取旧版本存下的页签时，把已并入合并页的单页页签归到合并页（同组只留一个）。
      merge: (persisted, current) => {
        const saved = (persisted ?? {}) as Partial<Pick<TabsState, 'tabs' | 'activeKey' | 'pinTabsBar'>>
        // 存下来的顺序再按「固定 → 锁定 → 普通」归整一次，防止手改存储或旧数据导致分段错乱。
        const tabs = Array.isArray(saved.tabs) ? normalizeTabOrder(normalizePersistedTabs(saved.tabs)) : current.tabs
        const activeKey = saved.activeKey
          ? resolveRoute(saved.activeKey)?.meta.navGroup ?? saved.activeKey
          : current.activeKey
        return { ...current, ...saved, tabs, activeKey }
      },
      partialize: (state) => ({
        tabs: state.tabs,
        activeKey: state.activeKey,
        pinTabsBar: state.pinTabsBar,
      }),
    },
  ),
)
