import { create } from 'zustand'
import { persist } from 'zustand/middleware'

interface QuickLinksState {
  /**
   * 工作台「常用导航」：按用户名（小写）分别记下固定的页面 key（即菜单路径）。
   * 没有某个用户的记录 = 从没调整过，页面按默认规则取；空数组 = 用户主动清空。
   */
  pinnedByUser: Record<string, string[]>
  setPinned: (userKey: string, keys: string[]) => void
  resetPinned: (userKey: string) => void
}

export function toQuickLinkUserKey(username?: string | null): string {
  return (username ?? '').trim().toLowerCase() || 'anonymous'
}

export const useQuickLinksStore = create<QuickLinksState>()(
  persist(
    (set) => ({
      pinnedByUser: {},
      setPinned: (userKey, keys) =>
        set((state) => ({ pinnedByUser: { ...state.pinnedByUser, [userKey]: keys } })),
      resetPinned: (userKey) =>
        set((state) => {
          const next = { ...state.pinnedByUser }
          delete next[userKey]
          return { pinnedByUser: next }
        }),
    }),
    {
      name: 'hb-admin-quick-links-v1',
      partialize: (state) => ({ pinnedByUser: state.pinnedByUser }),
    },
  ),
)
