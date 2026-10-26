import type { MenuProps } from 'antd'
import { useEffect, useState } from 'react'

// 侧栏「考勤审核」菜单的待审数角标。
// 服务层按需动态加载，不进首屏包；路由切换时最多每分钟刷新一次，审核页批准 / 拒绝后发事件立即刷新。
export const ATTENDANCE_REVIEW_MENU_KEY = '/pos-admin/attendance-review'
export const ATTENDANCE_REVIEW_CHANGED_EVENT = 'hb:attendance-review-changed'
const REFRESH_INTERVAL_MS = 60_000

export function notifyAttendanceReviewChanged(pendingCount?: number) {
  window.dispatchEvent(new CustomEvent(ATTENDANCE_REVIEW_CHANGED_EVENT, { detail: pendingCount }))
}

export function useAttendancePendingCount(enabled: boolean, pathname: string): number {
  const [count, setCount] = useState(0)
  const [lastLoadedAt, setLastLoadedAt] = useState(0)

  useEffect(() => {
    if (!enabled || Date.now() - lastLoadedAt < REFRESH_INTERVAL_MS) return
    let cancelled = false
    setLastLoadedAt(Date.now())
    void import('../services/attendancePayrollService')
      .then((service) => service.getAttendancePendingCount())
      .then((value) => {
        if (!cancelled) setCount(value)
      })
      // 角标是辅助信息：失败（无权限、网络）时保持原值，不打扰用户
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
    // 只在路由变化时判断是否该刷新
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, pathname])

  useEffect(() => {
    if (!enabled) return
    const handleChanged = (event: Event) => {
      const detail = (event as CustomEvent<number | undefined>).detail
      if (typeof detail === 'number') {
        setCount(detail)
      } else {
        // 没带数字时让下一次路由变化重新拉
        setLastLoadedAt(0)
      }
    }
    window.addEventListener(ATTENDANCE_REVIEW_CHANGED_EVENT, handleChanged)
    return () => window.removeEventListener(ATTENDANCE_REVIEW_CHANGED_EVENT, handleChanged)
  }, [enabled])

  return enabled ? count : 0
}

type MenuItems = NonNullable<MenuProps['items']>

/** 给指定菜单项的文字后面挂一个数字角标（递归查找子菜单）。 */
export function withMenuCountBadge(items: MenuProps['items'], key: string, count: number): MenuProps['items'] {
  if (!items || count <= 0) return items
  return items.map((item) => {
    if (!item || typeof item !== 'object') return item
    const node = item as MenuItems[number] & { key?: unknown; label?: unknown; children?: MenuItems }
    if (node.key === key) {
      return {
        ...node,
        label: (
          <span className="admin-menu-count-label">
            <span>{node.label as string}</span>
            <span className="admin-menu-count">{count > 99 ? '99+' : count}</span>
          </span>
        ),
      } as MenuItems[number]
    }
    if (node.children) {
      return { ...node, children: withMenuCountBadge(node.children, key, count) } as MenuItems[number]
    }
    return item
  })
}
