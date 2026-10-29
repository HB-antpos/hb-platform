import { AppstoreOutlined, EditOutlined, PlusOutlined, StarFilled, StarOutlined } from '@ant-design/icons'
import { Avatar, Button, Empty, Segmented } from 'antd'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router-dom'
import { ATTENDANCE_REVIEW_MENU_KEY, useAttendancePendingCount } from '../../layout/attendancePendingBadge'
import { buildMenus } from '../../router/routes'
import { useAuthStore } from '../../store/auth'
import { toQuickLinkUserKey, useQuickLinksStore } from '../../store/quickLinks'
import { useTabsStore } from '../../store/tabs'
import {
  DASHBOARD_PATH,
  QUICK_LINK_LIMIT,
  flattenMenuLinks,
  getGreetingPeriod,
  groupLinksByModule,
  pinKey,
  resolvePinnedKeys,
  unpinKey,
  type QuickLinkEntry,
} from './quickLinks'
import QuickLinkManager from './QuickLinkManager'
import './Dashboard.css'

// 管理抽屉不能再用 lazy() 拆出去：它和工作台共用 quickLinks.ts，拆开后 Rollup 会把共享模块并进工作台 chunk，
// 工作台 chunk 就失去独立的页面级入口标识，包体积门禁（requiredDynamicEntries: dashboard）会失败。
// 抽屉本身只有几 KB，真正的依赖（antd 抽屉、dnd-kit）本来就在公共 chunk 里。

const RECENT_LIMIT = 6

type FeatureView = 'module' | 'name'

export default function DashboardPage() {
  const { currentUser, access, navigationMenu } = useAuthStore()
  const { t, i18n } = useTranslation()
  const location = useLocation()
  const tabs = useTabsStore((state) => state.tabs)

  const [managerOpen, setManagerOpen] = useState(false)
  const [featureView, setFeatureView] = useState<FeatureView>('module')

  // 候选页面直接取侧栏同一份（已按权限裁剪的）菜单，工作台与侧栏不会出现两套清单。
  const entries = useMemo(
    () => flattenMenuLinks(buildMenus(access, navigationMenu)),
    // 菜单标题随语言变化
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [access, navigationMenu, i18n.language],
  )
  const entryByKey = useMemo(() => new Map(entries.map((entry) => [entry.key, entry])), [entries])
  const modules = useMemo(() => groupLinksByModule(entries), [entries])
  const byName = useMemo(
    () => [...entries].sort((a, b) => a.title.localeCompare(b.title, i18n.language.startsWith('zh') ? 'zh-Hans-CN' : 'en')),
    [entries, i18n.language],
  )

  const userKey = toQuickLinkUserKey(currentUser?.username)
  const storedKeys = useQuickLinksStore((state) => state.pinnedByUser[userKey])
  const setPinned = useQuickLinksStore((state) => state.setPinned)
  const resetPinned = useQuickLinksStore((state) => state.resetPinned)
  const pinnedKeys = useMemo(() => resolvePinnedKeys(entries, storedKeys), [entries, storedKeys])
  const pinnedSet = new Set(pinnedKeys)
  const pinnedEntries = pinnedKeys.map((key) => entryByKey.get(key)).filter((entry): entry is QuickLinkEntry => Boolean(entry))
  const atLimit = pinnedKeys.length >= QUICK_LINK_LIMIT

  // 待审数角标：只在工作台处于前台时拉取（页面被保活缓存，后台时不跟着路由变化重复请求）。
  const isForeground = location.pathname === DASHBOARD_PATH
  const attendancePending = useAttendancePendingCount(access.canReviewAttendance && isForeground, isForeground ? DASHBOARD_PATH : '')

  // 页签 store 没有时间戳，页签按打开顺序追加，倒过来就是最近打开在前。
  const recentTabs = useMemo(
    () => tabs.filter((tab) => !tab.affix).slice().reverse().slice(0, RECENT_LIMIT),
    [tabs],
  )

  const username = currentUser?.username || '--'
  const roleNames = currentUser?.roleNames ?? []
  const roleCount = roleNames.length
  const permCount = currentUser?.permissions?.length ?? 0
  const storeCount = currentUser?.stores?.length ?? 0

  const now = new Date()
  const greeting = t(`dashboard.greeting.${getGreetingPeriod(now.getHours())}`, { name: username })
  const dateText = new Intl.DateTimeFormat(i18n.language.startsWith('zh') ? 'zh-CN' : 'en-AU', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    weekday: 'long',
  }).format(now)

  const openManager = () => setManagerOpen(true)
  const updatePinned = (keys: string[]) => setPinned(userKey, keys)
  const togglePin = (key: string) => updatePinned(pinnedSet.has(key) ? unpinKey(pinnedKeys, key) : pinKey(pinnedKeys, key))

  const renderStar = (entry: QuickLinkEntry) => {
    const pinned = pinnedSet.has(entry.key)
    const disabled = !pinned && atLimit
    const label = pinned
      ? t('dashboard.unpin', { title: entry.title })
      : disabled
        ? t('dashboard.limitReached', { max: QUICK_LINK_LIMIT })
        : t('dashboard.pin', { title: entry.title })
    return (
      <button
        type="button"
        className="workbench-star"
        data-pinned={pinned}
        aria-pressed={pinned}
        aria-label={label}
        title={label}
        disabled={disabled}
        onClick={() => togglePin(entry.key)}
      >
        {pinned ? <StarFilled /> : <StarOutlined />}
      </button>
    )
  }

  return (
    <div className="workbench">
      <section className="workbench-card workbench-hero">
        <div>
          <h1>{greeting}</h1>
          <p>
            {dateText} · {t('dashboard.heroHint')}
          </p>
        </div>
        <dl className="workbench-facts">
          <div>
            <dt>{t('dashboard.factRoles')}</dt>
            <dd>{roleCount}</dd>
          </div>
          <div>
            <dt>{t('dashboard.factPermissions')}</dt>
            <dd>{permCount}</dd>
          </div>
          <div>
            <dt>{t('dashboard.factStores')}</dt>
            <dd>{storeCount}</dd>
          </div>
        </dl>
      </section>

      <div className="workbench-grid">
        <div className="workbench-col">
          <section className="workbench-card" aria-labelledby="workbench-quick-title">
            <div className="workbench-sec-head">
              <h2 id="workbench-quick-title">{t('dashboard.quickLinks')}</h2>
              <span className="workbench-hint">
                {t('dashboard.quickLinksCount', { count: pinnedEntries.length, max: QUICK_LINK_LIMIT })}
              </span>
              <Button className="workbench-sec-action" size="small" icon={<EditOutlined />} onClick={openManager}>
                {t('dashboard.manage')}
              </Button>
            </div>
            {pinnedEntries.length === 0 && entries.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dashboard.noAccessiblePages')} />
            ) : (
              <div className="workbench-tiles">
                {pinnedEntries.map((entry) => {
                  const badge = entry.key === ATTENDANCE_REVIEW_MENU_KEY ? attendancePending : 0
                  return (
                    <Link key={entry.key} to={entry.key} className="workbench-tile">
                      <span className="workbench-tile-icon">{entry.icon ?? <AppstoreOutlined />}</span>
                      <span className="workbench-tile-name">{entry.title}</span>
                      {entry.module !== entry.title ? <span className="workbench-tile-module">{entry.module}</span> : null}
                      {badge > 0 ? (
                        <span className="workbench-tile-badge" aria-label={t('dashboard.pendingCount', { count: badge })}>
                          {badge > 99 ? '99+' : badge}
                        </span>
                      ) : null}
                    </Link>
                  )
                })}
                {!atLimit ? (
                  <button type="button" className="workbench-tile workbench-tile-add" onClick={openManager}>
                    <PlusOutlined />
                    {t('dashboard.addQuickLink')}
                  </button>
                ) : null}
              </div>
            )}
          </section>

          <section className="workbench-card" aria-labelledby="workbench-all-title">
            <div className="workbench-sec-head">
              <h2 id="workbench-all-title">{t('dashboard.allFeatures')}</h2>
              <span className="workbench-hint">{t('dashboard.allFeaturesHint')}</span>
              <Segmented<FeatureView>
                className="workbench-sec-action"
                size="small"
                value={featureView}
                onChange={setFeatureView}
                options={[
                  { value: 'module', label: t('dashboard.viewByModule') },
                  { value: 'name', label: t('dashboard.viewByName') },
                ]}
              />
            </div>
            {featureView === 'module' ? (
              <div className="workbench-modules">
                {modules.map((group) => (
                  <div key={group.key} className="workbench-module">
                    <h3>
                      {group.icon}
                      {group.title}
                    </h3>
                    {group.links.map((entry) => (
                      <div key={entry.key} className="workbench-link-row">
                        <Link to={entry.key}>{entry.title}</Link>
                        {renderStar(entry)}
                      </div>
                    ))}
                  </div>
                ))}
              </div>
            ) : (
              <div className="workbench-modules workbench-modules-flat">
                {byName.map((entry) => (
                  <div key={entry.key} className="workbench-link-row">
                    <Link to={entry.key}>
                      {entry.title}
                      {entry.module !== entry.title ? <small>{entry.module}</small> : null}
                    </Link>
                    {renderStar(entry)}
                  </div>
                ))}
              </div>
            )}
          </section>
        </div>

        <div className="workbench-col">
          <section className="workbench-card" aria-labelledby="workbench-recent-title">
            <div className="workbench-sec-head">
              <h2 id="workbench-recent-title">{t('dashboard.recent')}</h2>
              <span className="workbench-hint">{t('dashboard.recentHint')}</span>
            </div>
            {recentTabs.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dashboard.recentEmpty')} />
            ) : (
              <ul className="workbench-recent">
                {recentTabs.map((tab) => {
                  const entry = entryByKey.get(tab.key)
                  return (
                    <li key={tab.key}>
                      <Link to={tab.path} className="workbench-recent-row">
                        <span className="workbench-recent-icon">{entry?.icon ?? <AppstoreOutlined />}</span>
                        <span className="workbench-recent-title">{tab.title}</span>
                        {entry && entry.module !== entry.title ? (
                          <span className="workbench-recent-module">{entry.module}</span>
                        ) : null}
                      </Link>
                    </li>
                  )
                })}
              </ul>
            )}
          </section>

          <section className="workbench-card workbench-account" aria-labelledby="workbench-account-title">
            <div className="workbench-account-who">
              <Avatar size={44}>{username.slice(0, 1).toUpperCase()}</Avatar>
              <div>
                <strong id="workbench-account-title">{username}</strong>
                <span>{t('dashboard.account')}</span>
              </div>
            </div>
            <div className="workbench-chips">
              {roleNames.length === 0 ? (
                <span className="workbench-chip">{t('dashboard.noRoleInfo')}</span>
              ) : (
                roleNames.map((role) => (
                  <span key={role} className="workbench-chip" data-strong={role.toLowerCase() === 'admin'}>
                    {role}
                  </span>
                ))
              )}
            </div>
            <dl className="workbench-account-kv">
              <div>
                <dd>{permCount}</dd>
                <dt>{t('dashboard.permCount')}</dt>
              </div>
              <div>
                <dd>{storeCount}</dd>
                <dt>{t('dashboard.storeCount')}</dt>
              </div>
            </dl>
          </section>
        </div>
      </div>

      <QuickLinkManager
        open={managerOpen}
        entries={entries}
        pinnedKeys={pinnedKeys}
        onChange={updatePinned}
        onReset={() => resetPinned(userKey)}
        onClose={() => setManagerOpen(false)}
      />
    </div>
  )
}
