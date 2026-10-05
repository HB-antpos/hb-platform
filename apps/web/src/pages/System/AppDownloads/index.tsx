import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { Alert, Button, Card, Space, Typography, theme } from 'antd'
import {
  AppstoreOutlined,
  DesktopOutlined,
  MobileOutlined,
  ReloadOutlined,
  ScanOutlined,
  TabletOutlined,
  ToolOutlined,
} from '@ant-design/icons'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { useAuthStore } from '../../../store/auth'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import WpfVersionsPage from '../WpfVersions'
import AppUpdatePolicyPanel from './AppUpdatePolicyPanel'
import AndroidApkBuildsPanel from './AndroidApkBuildsPanel'
import ReleaseOverview from './ReleaseOverview'
import ServiceApiTokensPanel from './ServiceApiTokensPanel'
import RustDeskDownloadEntry from './RustDeskDownloadEntry'
import {
  RELEASE_TERMINAL_LANES,
  RELEASE_TERMINALS,
  buildReleaseCenterSearch,
  isReleaseTerminal,
  resolveReleaseCenterLocation,
  type ReleaseCenterView,
  type ReleaseTerminal,
} from './releaseCenterLogic'
import releaseCenterMessagesEn from './releaseCenterMessages.en.json'
import releaseCenterMessagesZh from './releaseCenterMessages.zh.json'

registerPageMessages({ zh: releaseCenterMessagesZh, en: releaseCenterMessagesEn })

const TERMINAL_ICONS: Record<ReleaseTerminal, ReactNode> = {
  mobile: <MobileOutlined />,
  ipad: <TabletOutlined />,
  handheld: <ScanOutlined />,
  wpf: <DesktopOutlined />,
}

/**
 * 版本发布中心：原「App 下载」与「WPF 版本」合并页。
 * 左侧按终端导航，总览矩阵汇总全部轨道当前策略；位置写进地址栏（view / lane），刷新与分享都能还原。
 */
export default function AppDownloadsPage() {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const canManageAppDownloads = useAuthStore((state) => state.access.canManageAppDownloads)
  const isAdmin = useAuthStore((state) => state.access.isAdmin)
  const canViewTools = isAdmin || canManageAppDownloads
  const [searchParams, setSearchParams] = useSearchParams()
  const [refreshVersion, setRefreshVersion] = useState(0)
  // 手机宽度下导航改为一行横向滑动的紧凑按钮，不占满首屏。
  const compactNav = useIsMobile()
  const { view, lane } = resolveReleaseCenterLocation(searchParams, { canViewTools })

  const navigate = (nextView: ReleaseCenterView, nextLane?: string | null) => {
    setSearchParams(new URLSearchParams(buildReleaseCenterSearch(nextView, nextLane)), { replace: true })
    if (nextView !== view) {
      // 换到另一个视图时回到顶部，否则会停在上一视图的滚动位置（同一页签内滚动不会自动复位）。
      window.scrollTo({ top: 0 })
    }
  }

  const navButton = (key: ReleaseCenterView, icon: ReactNode, label: string, sub?: string) => {
    const active = view === key
    return (
      <button
        key={key}
        type="button"
        className="release-center-nav"
        aria-current={active ? 'page' : undefined}
        onClick={() => navigate(key)}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: compactNav ? 6 : 10,
          width: compactNav ? 'auto' : '100%',
          flex: compactNav ? 'none' : undefined,
          whiteSpace: compactNav ? 'nowrap' : undefined,
          minHeight: 44,
          padding: '8px 10px',
          border: 'none',
          borderRadius: token.borderRadius,
          background: active ? token.colorPrimaryBg : 'transparent',
          color: active ? token.colorPrimaryText : token.colorText,
          fontWeight: active ? 600 : 400,
          textAlign: 'left',
          cursor: 'pointer',
          fontFamily: 'inherit',
          fontSize: 'inherit',
        }}
      >
        <span style={{ fontSize: 16, display: 'inline-flex' }}>{icon}</span>
        <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <span>{label}</span>
          {sub && !compactNav ? (
            <span style={{ fontSize: 12, fontWeight: 400, color: token.colorTextSecondary }}>{sub}</span>
          ) : null}
        </span>
      </button>
    )
  }

  const sectionLabel = (text: string) => (compactNav ? null : (
    <div style={{ padding: '14px 10px 4px', fontSize: 12, color: token.colorTextSecondary }}>{text}</div>
  ))

  const renderLane = (terminal: ReleaseTerminal, currentLane: string) => {
    const common = { canManage: canManageAppDownloads, refreshVersion }
    switch (`${terminal}:${currentLane}`) {
      case 'mobile:ios-native':
        return <AppUpdatePolicyPanel {...common} lane="mobile-native" />
      case 'mobile:android-native':
        // 安卓原生策略只设最低支持构建号，目标就是最新公开 APK，所以两块放在同一轨道。
        return (
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <AppUpdatePolicyPanel {...common} lane="mobile-android-native" />
            <AndroidApkBuildsPanel appKey="mobile" refreshVersion={refreshVersion} />
          </Space>
        )
      case 'mobile:ota':
        return <AppUpdatePolicyPanel {...common} lane="mobile-ota" />
      case 'ipad:ios-native':
        return <AppUpdatePolicyPanel {...common} lane="ipad-native" />
      case 'ipad:ota':
        return <AppUpdatePolicyPanel {...common} lane="ipad-ota" />
      case 'handheld:policy':
        return <AppUpdatePolicyPanel {...common} lane="pos-handheld" />
      case 'handheld:apk':
        return <AndroidApkBuildsPanel appKey="pos-handheld" refreshVersion={refreshVersion} />
      case 'wpf:installer':
        // WPF 页自管加载与刷新；外部刷新时整体重挂载即可拿到最新数据。
        return <WpfVersionsPage key={refreshVersion} />
      default:
        return null
    }
  }

  const renderTerminal = (terminal: ReleaseTerminal) => {
    const lanes: readonly string[] = RELEASE_TERMINAL_LANES[terminal]
    const currentLane = lane && lanes.includes(lane) ? lane : lanes[0]
    return (
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Card
          title={(
            <Space size={8}>
              {TERMINAL_ICONS[terminal]}
              {t(`system.releaseCenter.nav.${terminal}`)}
            </Space>
          )}
          tabList={lanes.length > 1
            ? lanes.map((key) => ({ key, label: t(`system.releaseCenter.lanes.${key}`) }))
            : undefined}
          activeTabKey={currentLane}
          onTabChange={(key) => navigate(terminal, key)}
          styles={{ body: { padding: '12px 24px' } }}
        >
          <Typography.Text type="secondary">{t(`system.releaseCenter.terminalDesc.${terminal}`)}</Typography.Text>
        </Card>
        {/* key 让切换轨道时卸载旧轨道，旧请求随之失效，不会串到新轨道。 */}
        <div key={`${terminal}:${currentLane}`}>{renderLane(terminal, currentLane)}</div>
      </Space>
    )
  }

  return (
    <div
      style={{
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'flex-start',
        gap: 16,
      }}
    >
      {/* 导航按钮的悬停与键盘焦点样式：内联样式无法表达伪类。 */}
      <style>{`
        .release-center-nav:hover { background: ${token.colorFillTertiary} !important; }
        .release-center-nav[aria-current="page"]:hover { background: ${token.colorPrimaryBgHover} !important; }
        .release-center-nav:focus-visible { outline: 2px solid ${token.colorPrimary}; outline-offset: 1px; }
      `}</style>

      <nav
        aria-label={t('system.releaseCenter.navLabel')}
        style={{
          flex: compactNav ? '1 1 100%' : '1 1 220px',
          minWidth: 0,
          display: 'flex',
          flexDirection: compactNav ? 'row' : 'column',
          overflowX: compactNav ? 'auto' : undefined,
          gap: 2,
          padding: 10,
          background: token.colorBgContainer,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadiusLG,
        }}
      >
        {navButton('overview', <AppstoreOutlined />, t('system.releaseCenter.nav.overview'))}
        {sectionLabel(t('system.releaseCenter.nav.terminals'))}
        {RELEASE_TERMINALS.map((terminal) => navButton(
          terminal,
          TERMINAL_ICONS[terminal],
          t(`system.releaseCenter.nav.${terminal}`),
          t(`system.releaseCenter.nav.${terminal}Sub`),
        ))}
        {canViewTools ? (
          <>
            {sectionLabel(t('system.releaseCenter.nav.tools'))}
            {navButton(
              'tools',
              <ToolOutlined />,
              t('system.releaseCenter.nav.toolsItem'),
              t('system.releaseCenter.nav.toolsSub'),
            )}
          </>
        ) : null}
      </nav>

      <main style={{ flex: '999 1 560px', minWidth: 0 }}>
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <div
            style={{
              display: 'flex',
              flexWrap: 'wrap',
              alignItems: 'flex-end',
              justifyContent: 'space-between',
              gap: 12,
            }}
          >
            <div style={{ minWidth: 0 }}>
              <Typography.Title level={4} style={{ margin: 0 }}>
                {t('system.releaseCenter.title')}
              </Typography.Title>
              <Typography.Text type="secondary">{t('system.releaseCenter.subtitle')}</Typography.Text>
            </div>
            <Button icon={<ReloadOutlined />} onClick={() => setRefreshVersion((value) => value + 1)}>
              {t('system.releaseCenter.refresh')}
            </Button>
          </div>

          {!canManageAppDownloads ? (
            <Alert type="info" showIcon message={t('system.releaseCenter.readOnly')} />
          ) : null}

          {view === 'overview' ? (
            <ReleaseOverview
              refreshVersion={refreshVersion}
              onOpenLane={(terminal, nextLane) => navigate(terminal, nextLane)}
            />
          ) : null}

          {isReleaseTerminal(view) ? renderTerminal(view) : null}

          {view === 'tools' ? (
            <Space direction="vertical" size={16} style={{ width: '100%' }}>
              {isAdmin ? <RustDeskDownloadEntry /> : null}
              {canManageAppDownloads ? <ServiceApiTokensPanel /> : null}
            </Space>
          ) : null}
        </Space>
      </main>
    </div>
  )
}
