import { Card, Modal, Tabs } from 'antd'
import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useSearchParams } from 'react-router-dom'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'

import LogsTab from './LogsTab'
import TemplatesTab from './TemplatesTab'
import pageMessagesEn from './staffNotificationPageMessages.en.json'
import pageMessagesZh from './staffNotificationPageMessages.zh.json'
import './staffNotificationPage.css'

// 页面级文案随本页代码块懒注册，不进首屏 i18n 包（逐人状态、批次结果两个共用标签的文案由它们自己注册）。
registerPageMessages({ zh: pageMessagesZh, en: pageMessagesEn })

export const STAFF_NOTIFICATIONS_PATH = '/system/staff-notifications'

type StaffNotificationsTab = 'templates' | 'logs'

/** URL 里的 ?tab=logs（发送结果弹窗的「查看发送记录」会跳过来）；其它取值一律按默认的「模板」。 */
function parseTab(value: string | null): StaffNotificationsTab | null {
  return value === 'logs' || value === 'templates' ? value : null
}

/**
 * 员工通知：「模板」管理邮件 / 短信文案，「发送记录」按批次查看逐人结果。
 * 本页是保活页签：被缓存在后台时 URL 已经是别的页面，所以只在地址确实是本页时才读 / 写 ?tab=。
 */
export default function StaffNotificationsPage() {
  const { t } = useTranslation()
  const location = useLocation()
  const [searchParams, setSearchParams] = useSearchParams()
  const isActivePath = location.pathname.replace(/\/+$/, '') === STAFF_NOTIFICATIONS_PATH
  const urlTab = isActivePath ? parseTab(searchParams.get('tab')) : null

  const [activeTab, setActiveTab] = useState<StaffNotificationsTab>(urlTab ?? 'templates')
  const [templatesDirty, setTemplatesDirty] = useState(false)
  // 放弃未保存修改后让模板页签重新挂载，编辑区回到已保存的版本。
  const [templatesVersion, setTemplatesVersion] = useState(0)

  // 外部带 ?tab= 跳进来（例如从发送结果弹窗）时跟着切换；这是导航行为，不再拦截。
  useEffect(() => {
    if (urlTab) setActiveTab(urlTab)
  }, [urlTab])

  const switchTab = useCallback((key: StaffNotificationsTab) => {
    setActiveTab(key)
    if (isActivePath) {
      const next = new URLSearchParams(searchParams)
      next.set('tab', key)
      setSearchParams(next, { replace: true })
    }
  }, [isActivePath, searchParams, setSearchParams])

  const handleTabChange = (key: string) => {
    const next: StaffNotificationsTab = key === 'logs' ? 'logs' : 'templates'
    if (next === activeTab) return
    if (activeTab === 'templates' && templatesDirty) {
      Modal.confirm({
        title: t('system.staffNotifications.page.discardTitle', '放弃未保存的修改？'),
        content: t('system.staffNotifications.page.discardContent', '模板还有没保存的修改，切换页签后这些修改会丢失。'),
        okText: t('system.staffNotifications.page.discardOk', '放弃修改'),
        okButtonProps: { danger: true },
        cancelText: t('system.staffNotifications.page.discardCancel', '继续编辑'),
        onOk: () => {
          setTemplatesDirty(false)
          setTemplatesVersion((version) => version + 1)
          switchTab(next)
        },
      })
      return
    }
    switchTab(next)
  }

  return (
    <PageContainer
      compact
      title={t('system.staffNotifications.page.title', '员工通知')}
      subtitle={t('system.staffNotifications.page.subtitle', '用模板给员工发邮件、短信')}
    >
      <Card>
        <Tabs
          activeKey={activeTab}
          onChange={handleTabChange}
          tabBarStyle={{ marginBottom: 14 }}
          items={[
            {
              key: 'templates',
              label: t('system.staffNotifications.page.tabTemplates', '模板'),
              children: <TemplatesTab key={templatesVersion} onDirtyChange={setTemplatesDirty} />,
            },
            {
              key: 'logs',
              label: t('system.staffNotifications.page.tabLogs', '发送记录'),
              children: <LogsTab />,
            },
          ]}
        />
      </Card>
    </PageContainer>
  )
}
