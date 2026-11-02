import { Alert, Skeleton, Tabs, Tag, message } from 'antd'
import { Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  createMyobAuthorizeUrl,
  getMyobCompanies,
  getMyobConnections,
  getMyobStoreMappings,
} from '../../../services/myobService'
import { useAuthStore } from '../../../store/auth'
import type { MyobBindingCounts, MyobCompanyOption, MyobConnection, MyobStoreMappings } from '../../../types/myob'
import ConnectionsPanel from './ConnectionsPanel'
import StoreMappingsPanel from './StoreMappingsPanel'
import {
  getPendingBindingCount,
  getVisibleMyobTabs,
  resolveActiveMyobTab,
  type MyobTabKey,
} from './myobEmployeeLogic'
import {
  extractMyobError,
  getMyobErrorMessageKey,
  parseMyobCallback,
  stripMyobCallbackParams,
  upsertConnection,
} from './myobLogic'
import messagesEn from './myobMessages.en.json'
import messagesZh from './myobMessages.zh.json'
import './myob.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

// 员工绑定 / 发薪公司 / 推送与对账页签、工资类别映射卡片只在有权限时才渲染，且各自单独成块懒加载（页面级块之外不增加首屏体积）
const EmployeeBindingTab = lazy(() => import('./EmployeeBindingTab'))
const PayrollEmployersTab = lazy(() => import('./PayrollEmployersTab'))
const PayCategoryMappingCard = lazy(() => import('./PayCategoryMappingCard'))
const PushPreviewTab = lazy(() => import('./PushPreviewTab'))

function TabFallback() {
  return <Skeleton active paragraph={{ rows: 6 }} />
}

const EMPTY_MAPPINGS: MyobStoreMappings = { stores: [], connections: [] }

export default function MyobPage() {
  const { t } = useTranslation()
  const location = useLocation()
  const navigate = useNavigate()
  const access = useAuthStore((state) => state.access)
  // 各页签按各自权限显示：连接与映射 = Myob.Connection.Manage，员工绑定 = Myob.Employee.Bind，发薪公司 = 编辑工资
  const visibleTabs = useMemo(
    () =>
      getVisibleMyobTabs({
        canManageConnections: access.canManageMyobConnections,
        canBindEmployees: access.canBindMyobEmployees,
        canEditPayroll: access.canEditAttendancePayroll,
      }),
    [access.canBindMyobEmployees, access.canEditAttendancePayroll, access.canManageMyobConnections],
  )
  // 授权回跳（?myob=）要落在「连接与映射」，其余按第一个可见页签
  const [requestedTab, setRequestedTab] = useState<MyobTabKey | null>(() =>
    parseMyobCallback(location.search) ? 'connections' : null,
  )
  const activeTab = resolveActiveMyobTab(requestedTab, visibleTabs)
  const [bindingCounts, setBindingCounts] = useState<MyobBindingCounts | null>(null)
  const [focusRequest, setFocusRequest] = useState<{ connectionGuid: string; seq: number } | null>(null)
  // 推送预览里「去映射」带来的预选公司（工资类别映射卡片用）
  const [mappingFocus, setMappingFocus] = useState<{ connectionGuid: string; seq: number } | null>(null)

  const [connections, setConnections] = useState<MyobConnection[]>([])
  const [connectionsLoading, setConnectionsLoading] = useState(true)
  const [connectionsError, setConnectionsError] = useState<string | null>(null)
  const [mappings, setMappings] = useState<MyobStoreMappings>(EMPTY_MAPPINGS)
  const [mappingsLoading, setMappingsLoading] = useState(true)
  const [mappingsError, setMappingsError] = useState<string | null>(null)
  const [authorizing, setAuthorizing] = useState(false)
  // 公司选项（只读、只含 Active）：员工绑定 / 推送与对账页签的公司选择条用它，任一页签权限即可读，不依赖连接管理权限
  const [companies, setCompanies] = useState<MyobCompanyOption[]>([])
  const [companiesLoading, setCompaniesLoading] = useState(true)
  const [companiesError, setCompaniesError] = useState<string | null>(null)
  const companiesSeq = useRef(0)
  const needsCompanies = access.canBindMyobEmployees || access.canEditAttendancePayroll

  // 只认最后一次发起的请求，避免快速连点刷新时旧响应覆盖新数据
  const connectionsSeq = useRef(0)
  const mappingsSeq = useRef(0)

  const loadConnections = useCallback(async () => {
    const seq = ++connectionsSeq.current
    setConnectionsLoading(true)
    try {
      const data = await getMyobConnections()
      if (seq === connectionsSeq.current) {
        setConnections(data)
        setConnectionsError(null)
      }
    } catch (err) {
      if (seq === connectionsSeq.current) {
        setConnectionsError(extractMyobError(err, t('myob.connections.loadFailed')).message)
      }
    } finally {
      if (seq === connectionsSeq.current) {
        setConnectionsLoading(false)
      }
    }
  }, [t])

  const loadMappings = useCallback(async () => {
    const seq = ++mappingsSeq.current
    setMappingsLoading(true)
    try {
      const data = await getMyobStoreMappings()
      if (seq === mappingsSeq.current) {
        setMappings(data)
        setMappingsError(null)
      }
    } catch (err) {
      if (seq === mappingsSeq.current) {
        setMappingsError(extractMyobError(err, t('myob.mappings.loadFailed')).message)
      }
    } finally {
      if (seq === mappingsSeq.current) {
        setMappingsLoading(false)
      }
    }
  }, [t])

  const loadCompanies = useCallback(async () => {
    const seq = ++companiesSeq.current
    setCompaniesLoading(true)
    try {
      const data = await getMyobCompanies()
      if (seq === companiesSeq.current) {
        setCompanies(data)
        setCompaniesError(null)
      }
    } catch (err) {
      if (seq === companiesSeq.current) {
        setCompaniesError(extractMyobError(err, t('myob.employees.company.loadFailed')).message)
      }
    } finally {
      if (seq === companiesSeq.current) {
        setCompaniesLoading(false)
      }
    }
  }, [t])

  // 连接 / 映射接口只认 Myob.Connection.Manage：没有该权限时完全不请求，避免无谓的 403 提示
  useEffect(() => {
    if (access.canManageMyobConnections) {
      void loadConnections()
      void loadMappings()
    } else {
      connectionsSeq.current += 1
      mappingsSeq.current += 1
      setConnectionsLoading(false)
      setMappingsLoading(false)
    }
  }, [access.canManageMyobConnections, loadConnections, loadMappings])

  // 公司选项：员工绑定 / 推送与对账页签需要（有任一页签权限即可读）
  useEffect(() => {
    if (needsCompanies) {
      void loadCompanies()
    } else {
      companiesSeq.current += 1
      setCompaniesLoading(false)
    }
  }, [needsCompanies, loadCompanies])

  // 授权完成后后端会把浏览器重定向回本页并带上 ?myob=connected / error；
  // 读取一次、提示结果，然后用 replace 清掉参数，刷新或返回时不会重复提示。
  const handledCallback = useRef(false)
  useEffect(() => {
    if (handledCallback.current) {
      return
    }
    const result = parseMyobCallback(location.search)
    if (!result) {
      return
    }
    handledCallback.current = true
    if (result.kind === 'connected') {
      message.success(t('myob.callback.connected'))
    } else {
      message.error(t('myob.callback.error', { reason: t(getMyobErrorMessageKey(result.code)) }))
    }
    navigate({ pathname: location.pathname, search: stripMyobCallbackParams(location.search), hash: location.hash }, { replace: true })
  }, [location.hash, location.pathname, location.search, navigate, t])

  // 取授权地址后整页跳转到 MYOB；跳转前保持按钮禁用，失败才恢复
  const handleAuthorize = useCallback(async () => {
    if (authorizing) {
      return
    }
    setAuthorizing(true)
    try {
      const url = await createMyobAuthorizeUrl()
      // 只允许跳转 https 地址，防止后端响应被篡改成 javascript: 等
      if (!/^https:\/\//i.test(url)) {
        throw new Error('invalid authorize url')
      }
      window.location.href = url
    } catch (err) {
      message.error(extractMyobError(err, t('myob.connections.authorizeFailed')).message)
      setAuthorizing(false)
    }
  }, [authorizing, t])

  const handleGoBind = useCallback((connectionGuid: string) => {
    setFocusRequest((current) => ({ connectionGuid, seq: (current?.seq ?? 0) + 1 }))
    setRequestedTab('employees')
  }, [])

  const handleGoMapping = useCallback((connectionGuid: string) => {
    setMappingFocus((current) => ({ connectionGuid, seq: (current?.seq ?? 0) + 1 }))
    setRequestedTab('connections')
  }, [])

  const pendingCount = getPendingBindingCount(bindingCounts)

  const connectionsPane = (
    <div className="myob-tabpane">
      <Alert
        type="info"
        showIcon
        message={t('myob.notice.title')}
        description={
          <ul className="myob-notice-list">
            <li>{t('myob.notice.admin')}</li>
            <li>{t('myob.notice.perAbn')}</li>
          </ul>
        }
      />

      <ConnectionsPanel
        connections={connections}
        loading={connectionsLoading}
        error={connectionsError}
        stores={mappings.stores}
        authorizing={authorizing}
        onAuthorize={() => void handleAuthorize()}
        onReload={() => void loadConnections()}
        onConnectionUpdated={(updated) => {
          setConnections((current) => upsertConnection(current, updated))
          // 验证 / 断开可能改变连接状态，公司选项（只含 Active）同步刷新
          void loadCompanies()
        }}
        // 断开会移除该连接下的全部映射：重新拉映射表（可选连接列表也随之变化）
        onDisconnected={() => void loadMappings()}
      />

      <StoreMappingsPanel
        mappings={mappings}
        allConnections={connections}
        loading={mappingsLoading}
        error={mappingsError}
        onReload={() => void loadMappings()}
        onSaved={(saved) => {
          setMappings(saved)
          // 映射数量显示在连接表里，保存后同步刷新
          void loadConnections()
        }}
      />

      <Suspense fallback={<TabFallback />}>
        <PayCategoryMappingCard connections={connections} connectionsLoading={connectionsLoading} focusRequest={mappingFocus} />
      </Suspense>
    </div>
  )

  const tabItems = [
    visibleTabs.includes('connections')
      ? { key: 'connections', label: t('myob.tabs.connections'), children: connectionsPane }
      : null,
    visibleTabs.includes('employees')
      ? {
          key: 'employees',
          label: (
            <span>
              {t('myob.tabs.employees')}
              {pendingCount > 0 ? <span className="myob-tab-badge">{t('myob.tabs.pending', { count: pendingCount })}</span> : null}
            </span>
          ),
          // 始终渲染（即使不在当前页签）：这样待处理角标进入页面就能显示
          forceRender: true,
          children: (
            <Suspense fallback={<TabFallback />}>
              <EmployeeBindingTab
                companies={companies}
                companiesLoading={companiesLoading}
                companiesError={companiesError}
                canConnect={access.canManageMyobConnections}
                authorizing={authorizing}
                onAuthorize={() => void handleAuthorize()}
                onReloadCompanies={() => void loadCompanies()}
                focusRequest={focusRequest}
                onCountsChange={setBindingCounts}
                canCreateOurSide={access.canReadUserConsole}
              />
            </Suspense>
          ),
        }
      : null,
    visibleTabs.includes('payroll')
      ? {
          key: 'payroll',
          label: t('myob.tabs.payroll'),
          children: (
            <Suspense fallback={<TabFallback />}>
              <PayrollEmployersTab onGoBind={access.canBindMyobEmployees ? handleGoBind : undefined} />
            </Suspense>
          ),
        }
      : null,
    // 推送与对账：本批只读预览（推送按钮禁用）；权限 = 编辑工资（Attendance.Payroll.Edit）
    visibleTabs.includes('push')
      ? {
          key: 'push',
          label: (
            <span>
              {t('myob.tabs.push')}
              <Tag className="myob-tab-preview" color="processing">
                {t('myob.tabs.preview')}
              </Tag>
            </span>
          ),
          children: (
            <Suspense fallback={<TabFallback />}>
              <PushPreviewTab
                companies={companies}
                companiesLoading={companiesLoading}
                companiesError={companiesError}
                onReloadCompanies={() => void loadCompanies()}
                active={activeTab === 'push'}
                canGoBind={access.canBindMyobEmployees}
                canGoMapping={access.canManageMyobConnections}
                canPush={access.canPushMyobTimesheet}
                onGoBind={handleGoBind}
                onGoMapping={handleGoMapping}
              />
            </Suspense>
          ),
        }
      : null,
  ].filter((item): item is NonNullable<typeof item> => item !== null)

  return (
    <PageContainer title={t('myob.title')} subtitle={t('myob.subtitle')}>
      <div className="myob-page">
        {activeTab === null ? (
          <Alert type="warning" showIcon message={t('myob.tabs.noAccess')} />
        ) : (
          <Tabs
            className="myob-tabs"
            aria-label={t('myob.tabs.ariaLabel')}
            activeKey={activeTab}
            items={tabItems}
            onChange={(key) => setRequestedTab(key as MyobTabKey)}
          />
        )}
      </div>
    </PageContainer>
  )
}
