import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Spin, Tabs, Tag } from 'antd'
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getFaceMatchConfig } from '../../../services/faceMatchService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import AuditTab from './AuditTab'
import { describeFaceMatchError, getDisabledReasonKey, getVisibleTabs, isWriteBlocked, type FaceMatchTabKey } from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import ProfilesTab from './ProfilesTab'
import ResultsTab from './ResultsTab'
import ReviewTasksTab from './ReviewTasksTab'
import type { FaceMatchTabContext } from './shared'
import StaffTab from './StaffTab'
import StatsTab from './StatsTab'
import TrafficTab from './TrafficTab'
import UnwelcomeEntriesTab from './UnwelcomeEntriesTab'
import { useRemote } from './useRemote'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）；全局语言包只有菜单标题一个键。
registerPageMessages({ zh, en })

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const TAB_LABEL_KEYS: Record<FaceMatchTabKey, string> = {
  profiles: 'faceMatch.tabs.profiles',
  staff: 'faceMatch.tabs.staff',
  traffic: 'faceMatch.tabs.traffic',
  unwelcome: 'faceMatch.tabs.unwelcome',
  review: 'faceMatch.tabs.review',
  results: 'faceMatch.tabs.results',
  stats: 'faceMatch.tabs.stats',
  audit: 'faceMatch.tabs.audit',
}

export default function FaceMatchPage() {
  const { t } = useTranslation()
  const configLoader = useCallback((signal: AbortSignal) => getFaceMatchConfig(signal), [])
  const configState = useRemote(configLoader)
  const config = configState.data

  // 分店下拉只取一次；失败（例如账号没有分店查看权限）降级成手填分店编码。
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [storeOptionsFailed, setStoreOptionsFailed] = useState(false)
  const hasConfig = config !== null
  useEffect(() => {
    if (!hasConfig) return
    let cancelled = false
    getActiveStores()
      .then((options) => {
        if (!cancelled) setStoreOptions(options)
      })
      .catch(() => {
        if (!cancelled) setStoreOptionsFailed(true)
      })
    return () => {
      cancelled = true
    }
  }, [hasConfig])

  const tabKeys = useMemo(() => (config ? getVisibleTabs(config.permissions) : []), [config])
  // 受控页签：「抓拍与比对」的「去复核」要能切到「复核任务」。当前页签不在可见列表里（刚加载、权限变化）时回到第一个。
  const [selectedTab, setSelectedTab] = useState<FaceMatchTabKey | null>(null)
  const activeTab = selectedTab && tabKeys.includes(selectedTab) ? selectedTab : tabKeys[0]
  const hasReviewTab = tabKeys.includes('review')
  const context = useMemo<FaceMatchTabContext | null>(
    () =>
      config
        ? {
            config,
            storeOptions,
            storeOptionsFailed,
            writeBlocked: isWriteBlocked(config),
            goToReview: hasReviewTab ? () => setSelectedTab('review') : undefined,
            canViewTarget: hasReviewTab,
          }
        : null,
    [config, storeOptions, storeOptionsFailed, hasReviewTab],
  )

  const renderTab = (key: FaceMatchTabKey, tabContext: FaceMatchTabContext): ReactNode => {
    switch (key) {
      case 'profiles':
        return <ProfilesTab context={tabContext} />
      case 'staff':
        return <StaffTab context={tabContext} />
      case 'traffic':
        return <TrafficTab context={tabContext} />
      case 'unwelcome':
        return <UnwelcomeEntriesTab context={tabContext} />
      case 'review':
        return <ReviewTasksTab context={tabContext} />
      case 'results':
        return <ResultsTab context={tabContext} />
      case 'stats':
        return <StatsTab />
      case 'audit':
        return <AuditTab context={tabContext} />
    }
  }

  return (
    <PageContainer
      compact
      title={t('faceMatch.title')}
      subtitle={config?.region ? t('faceMatch.subtitle', { region: config.region }) : undefined}
      extra={
        <Button icon={<ReloadOutlined />} loading={configState.loading} onClick={configState.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      }
    >
      {/* 常驻提示：闸门只是记录，不能替代法律判断。 */}
      <Alert type="warning" showIcon message={t('faceMatch.legalNotice')} style={{ marginBottom: 12 }} />

      {configState.error && !config ? (
        <Alert
          type="error"
          showIcon
          message={describeFaceMatchError(t, configState.error)}
          action={<Button size="small" onClick={configState.reload}>{t('faceMatch.common.retry')}</Button>}
        />
      ) : !config || !context ? (
        <div style={{ padding: 48, textAlign: 'center' }}>
          <Spin />
        </div>
      ) : (
        <>
          {!config.enabled ? (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 12 }}
              message={t('faceMatch.disabled.title')}
              description={`${t(getDisabledReasonKey(config))} ${t('faceMatch.disabled.readOnly')}`}
            />
          ) : null}
          {tabKeys.length === 0 ? (
            <Empty description={t('faceMatch.noPermission')} />
          ) : (
            <Tabs
              destroyOnHidden
              activeKey={activeTab}
              onChange={(key) => setSelectedTab(key as FaceMatchTabKey)}
              items={tabKeys.map((key) => ({
                key,
                label:
                  key === 'results' ? (
                    <span>
                      {t(TAB_LABEL_KEYS[key])} <Tag color="red" style={{ marginInlineEnd: 0 }}>{t('faceMatch.tabs.newBadge')}</Tag>
                    </span>
                  ) : (
                    t(TAB_LABEL_KEYS[key])
                  ),
                children: renderTab(key, context),
              }))}
            />
          )}
        </>
      )}
    </PageContainer>
  )
}
