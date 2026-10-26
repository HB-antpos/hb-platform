import {
  LogoutOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  ReloadOutlined,
  ShoppingOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { Avatar, Breadcrumb, Button, Dropdown, Layout, Menu, Space, Typography } from 'antd'
import type { MenuProps } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import AppTabs from '../components/AppTabs'
import LanguageSwitch from '../components/LanguageSwitch'
import RouteLoadBoundary from '../components/RouteLoadBoundary'
import RouteKeepAlive, { type RouteKeepAliveRef } from '../components/RouteKeepAlive'
import { useIsMobile } from '../hooks/useIsMobile'
import MobileLayout from './MobileLayout'
import { resolveRouteKeepAliveState } from './adminKeepAlive'
import { ATTENDANCE_REVIEW_MENU_KEY, useAttendancePendingCount, withMenuCountBadge } from './attendancePendingBadge'
import {
  buildMenus,
  getBreadcrumbItems,
  getCurrentElement,
  getCurrentRoute,
  getOpenMenuKeys,
  getSelectedMenuKeys,
  toTabItem,
} from '../router/routes'
import { useAuthStore } from '../store/auth'
import { useTabsStore } from '../store/tabs'

const { Header, Sider, Content } = Layout

function DesktopAdminLayout() {
  const { t, i18n } = useTranslation()
  const [collapsed, setCollapsed] = useState(false)
  const [openKeys, setOpenKeys] = useState<string[]>([])
  const navigate = useNavigate()
  const location = useLocation()
  const keepAliveRef = useRef<RouteKeepAliveRef>(null)

  const { currentUser, access, navigationMenu, logout } = useAuthStore()
  const {
    tabs,
    pinTabsBar,
    setActiveKey,
    ensureTab,
    removeTab,
    removeOtherTabs,
    removeLeftTabs,
    removeRightTabs,
    resetTabs,
    updateTabTitle,
  } = useTabsStore()

  const routeTab = useMemo(() => toTabItem(location.pathname, access), [access, i18n.language, location.pathname])
  const currentRoute = getCurrentRoute(location.pathname, access)
  const currentTab = tabs.find((item) => item.key === routeTab?.key || item.key === currentRoute?.path) ?? routeTab
  const currentElement = getCurrentElement(location.pathname, access)
  const attendancePendingCount = useAttendancePendingCount(access.canReviewAttendance, location.pathname)
  const menus = useMemo(
    () => withMenuCountBadge(buildMenus(access, navigationMenu), ATTENDANCE_REVIEW_MENU_KEY, attendancePendingCount),
    [access, navigationMenu, i18n.language, attendancePendingCount],
  )
  const selectedKeys = getSelectedMenuKeys(location.pathname, access)
  // KeepAlive 必须跟随当前 URL，而不是提前变化的 tabs store activeKey。
  const { activeCacheKey, cacheKeys } = resolveRouteKeepAliveState({
    routeTab,
    tabs,
    fallbackPathname: location.pathname,
  })

  useEffect(() => {
    if (routeTab) {
      ensureTab(routeTab)
    }
    setActiveKey(routeTab?.key || location.pathname)
    setOpenKeys(getOpenMenuKeys(location.pathname, access))
  }, [access, ensureTab, location.pathname, routeTab, setActiveKey])

  useEffect(() => {
    const currentTabs = useTabsStore.getState().tabs
    for (const tab of currentTabs) {
      const updated = toTabItem(tab.path, access)
      if (updated) {
        updateTabTitle(tab.key, updated.title)
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [i18n.language])

  const handleMenuClick = ({ key }: { key: string }) => {
    navigate(key)
  }

  const handleRemoveTab = async (key: string) => {
    const nextPath = removeTab(key)
    await keepAliveRef.current?.destroy(key)
    navigate(nextPath)
  }

  const handleRemoveOtherTabs = async (key: string) => {
    const removedKeys = removeOtherTabs(key)
    if (removedKeys.length > 0) {
      await keepAliveRef.current?.destroy(removedKeys)
    }
    navigate(key)
  }

  const handleRemoveLeftTabs = async (key: string) => {
    const removedKeys = removeLeftTabs(key)
    if (removedKeys.length > 0) {
      await keepAliveRef.current?.destroy(removedKeys)
    }
    navigate(key)
  }

  const handleRemoveRightTabs = async (key: string) => {
    const removedKeys = removeRightTabs(key)
    if (removedKeys.length > 0) {
      await keepAliveRef.current?.destroy(removedKeys)
    }
    navigate(key)
  }

  const handleRefreshCurrent = () => {
    keepAliveRef.current?.refresh(activeCacheKey)
  }

  const handleLogout = async () => {
    await logout()
    resetTabs()
    navigate('/login', { replace: true })
  }

  const userMenuItems: MenuProps['items'] = [
    {
      key: 'refresh-profile',
      label: t('layout.refreshPage'),
      icon: <ReloadOutlined />,
      onClick: handleRefreshCurrent,
    },
    {
      key: 'logout',
      label: t('layout.logout'),
      icon: <LogoutOutlined />,
      onClick: () => {
        void handleLogout()
      },
    },
  ]

  return (
    <Layout className="admin-layout">
      <Sider className="admin-sider" trigger={null} collapsible collapsed={collapsed} width={248} theme="light">
        <div className="brand">
          <div className="brand-mark">HB</div>
          {!collapsed ? <span className="brand-text">HB Admin Platform</span> : null}
        </div>
        <Menu
          className="admin-side-menu"
          mode="inline"
          items={menus}
          selectedKeys={selectedKeys}
          // 折叠态不要受控 openKeys，否则 AntD 弹出子菜单会被立即收起。
          {...(!collapsed
            ? {
                openKeys,
                onOpenChange: (keys) => setOpenKeys(keys as string[]),
              }
            : {})}
          onClick={handleMenuClick}
        />
      </Sider>

      <Layout>
        <Header className="admin-header">
          <Space size={16}>
            <Button
              type="text"
              icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
              onClick={() => setCollapsed((value) => !value)}
            />
            <Breadcrumb
              items={getBreadcrumbItems(location.pathname, access, currentTab?.title)}
            />
          </Space>

          <Space size={12}>
            <LanguageSwitch className="admin-language-switch" />
            <Button
              type="primary"
              icon={<ShoppingOutlined />}
              onClick={() => window.open('/shop', '_blank')}
            >
              {t('layout.shopFront')}
            </Button>
            <Dropdown menu={{ items: userMenuItems }} placement="bottomRight" trigger={['click']}>
              <Space className="header-user">
                <Avatar icon={<UserOutlined />} />
                <div className="header-user-meta">
                  <Typography.Text strong>{currentUser?.username || '--'}</Typography.Text>
                  <Typography.Text type="secondary">{currentUser?.roleNames?.[0] || t('login.noRole')}</Typography.Text>
                </div>
              </Space>
            </Dropdown>
          </Space>
        </Header>

        <div className={`tabs-shell${pinTabsBar ? ' tabs-shell-pinned' : ''}`}>
          <AppTabs
            onRefreshCurrent={handleRefreshCurrent}
            onRemoveTab={handleRemoveTab}
            onRemoveOtherTabs={handleRemoveOtherTabs}
            onRemoveLeftTabs={handleRemoveLeftTabs}
            onRemoveRightTabs={handleRemoveRightTabs}
          />
        </div>

        <Content className="admin-content">
          <RouteKeepAlive
            ref={keepAliveRef}
            activeKey={activeCacheKey}
            include={cacheKeys}
            currentElement={(
              <RouteLoadBoundary resetKey={activeCacheKey}>
                {currentElement}
              </RouteLoadBoundary>
            )}
          />
        </Content>
      </Layout>
    </Layout>
  )
}

export default function AdminLayout() {
  const isMobile = useIsMobile()

  if (isMobile) {
    return <MobileLayout />
  }

  return <DesktopAdminLayout />
}
