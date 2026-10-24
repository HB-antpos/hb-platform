import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { TabItem } from '../types/router'
import { getAffixTabs, normalizePersistedTabs, resolveRoute } from '../router/routes'

interface TabsState {
  activeKey: string
  tabs: TabItem[]
  pinTabsBar: boolean
  setActiveKey: (key: string) => void
  setPinTabsBar: (value: boolean) => void
  ensureTab: (tab: TabItem) => void
  updateTabTitle: (key: string, title: string) => void
  moveTab: (activeKey: string, overKey: string) => void
  removeTab: (key: string) => string
  removeOtherTabs: (key: string) => string[]
  removeLeftTabs: (key: string) => string[]
  removeRightTabs: (key: string) => string[]
  resetTabs: () => void
}

const defaultTabs = getAffixTabs()
const defaultActiveKey = defaultTabs[0]?.key ?? '/dashboard'

function arrayMove<T>(items: T[], from: number, to: number) {
  const nextItems = [...items]
  const [target] = nextItems.splice(from, 1)
  nextItems.splice(to, 0, target)
  return nextItems
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
        if (activeKey === overKey) {
          return
        }

        const currentTabs = get().tabs
        const fixedTabs = currentTabs.filter((item) => item.affix)
        const movableTabs = currentTabs.filter((item) => !item.affix)
        const activeIndex = movableTabs.findIndex((item) => item.key === activeKey)
        const overIndex = movableTabs.findIndex((item) => item.key === overKey)

        if (activeIndex < 0 || overIndex < 0) {
          return
        }

        set({
          tabs: [...fixedTabs, ...arrayMove(movableTabs, activeIndex, overIndex)],
        })
      },
      removeTab: (key) => {
        const currentTabs = get().tabs
        const target = currentTabs.find((item) => item.key === key)

        if (!target || target.affix) {
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
      removeOtherTabs: (key) => {
        const keepKeys = new Set([...defaultTabs.map((item) => item.key), key])
        const removedKeys = get()
          .tabs.filter((item) => !keepKeys.has(item.key))
          .map((item) => item.key)

        set({
          tabs: get().tabs.filter((item) => keepKeys.has(item.key)),
          activeKey: key,
        })

        return removedKeys
      },
      removeLeftTabs: (key) => {
        const currentTabs = get().tabs
        const index = currentTabs.findIndex((item) => item.key === key)
        if (index <= 0) {
          return []
        }

        const removedKeys = currentTabs
          .slice(0, index)
          .filter((item) => !item.affix)
          .map((item) => item.key)

        set({
          tabs: currentTabs.filter((item, itemIndex) => item.affix || itemIndex >= index),
          activeKey: key,
        })

        return removedKeys
      },
      removeRightTabs: (key) => {
        const currentTabs = get().tabs
        const index = currentTabs.findIndex((item) => item.key === key)
        if (index < 0) {
          return []
        }

        const removedKeys = currentTabs
          .slice(index + 1)
          .filter((item) => !item.affix)
          .map((item) => item.key)

        set({
          tabs: currentTabs.filter((item, itemIndex) => item.affix || itemIndex <= index),
          activeKey: key,
        })

        return removedKeys
      },
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
        const tabs = Array.isArray(saved.tabs) ? normalizePersistedTabs(saved.tabs) : current.tabs
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
