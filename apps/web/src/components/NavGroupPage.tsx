import { Spin, Tabs } from 'antd'
import { Suspense, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import {
  getVisibleNavGroupTabs,
  matchNavGroupTab,
  shouldMountNavGroupTab,
} from '../router/navGroupLogic'
import { useAuthStore } from '../store/auth'
import type { NavGroupDefinition, NavGroupTab } from '../types/router'

interface NavGroupPageProps {
  group: NavGroupDefinition
}

const tabFallback = (
  <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
    <Spin />
  </div>
)

/**
 * 合并页外壳：同组的几个原页面共用一个侧栏菜单和一个顶部页签，页内用标签切换。
 * 每个标签仍是原来的路由地址，切换标签就是跳转地址，刷新、复制链接都能落到当前标签。
 * 本组件会被外层 KeepAlive 按组缓存：离开合并页时它仍挂着，地址不再属于本组，
 * 此时不保活的标签（凭据 / 摄像机地址 / 照片）立即卸载。
 */
export default function NavGroupPage({ group }: NavGroupPageProps) {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const location = useLocation()
  const navigate = useNavigate()
  const tabs = useMemo(() => getVisibleNavGroupTabs(group, access), [access, group])
  const currentTab = matchNavGroupTab(tabs, location.pathname)
  const groupInForeground = Boolean(currentTab)
  const [lastTabPath, setLastTabPath] = useState<string | undefined>(currentTab?.path)
  const [visited, setVisited] = useState<ReadonlySet<string>>(
    () => new Set(currentTab ? [currentTab.path] : []),
  )
  // 记住每个标签离开时的查询参数（如抽屉 ?id=），切回来时原样带上。
  const searchByTabRef = useRef(new Map<string, string>())

  useEffect(() => {
    if (!currentTab) {
      return
    }
    searchByTabRef.current.set(currentTab.path, location.search)
    setLastTabPath(currentTab.path)
    setVisited((previous) => {
      if (previous.has(currentTab.path)) return previous
      const next = new Set(previous)
      next.add(currentTab.path)
      return next
    })
  }, [currentTab, location.search])

  // 直接打开菜单地址（或当前标签已无权限）时，落到第一个有权限的标签。
  useEffect(() => {
    if (location.pathname !== group.path || !tabs.length) {
      return
    }
    navigate(tabs[0].path, { replace: true })
  }, [group.path, location.pathname, navigate, tabs])

  const activePath = currentTab?.path ?? lastTabPath ?? tabs[0]?.path

  const renderTabContent = (tab: NavGroupTab): ReactNode => (
    shouldMountNavGroupTab(tab, { activePath, groupInForeground, visited })
      ? <Suspense fallback={tabFallback}>{tab.element}</Suspense>
      : null
  )

  if (!tabs.length) {
    return null
  }

  // 只有一个标签有权限时不显示标签栏，直接展示该页面。
  if (tabs.length === 1) {
    return renderTabContent(tabs[0])
  }

  return (
    <Tabs
      className="nav-group-tabs"
      activeKey={activePath}
      onChange={(path) => navigate(`${path}${searchByTabRef.current.get(path) ?? ''}`)}
      tabBarStyle={{ marginBottom: 12 }}
      items={tabs.map((tab) => ({
        key: tab.path,
        label: t(tab.title),
        children: renderTabContent(tab),
      }))}
    />
  )
}
