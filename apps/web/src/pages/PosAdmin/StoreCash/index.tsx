import { ShopOutlined } from '@ant-design/icons'
import { Alert, Empty, Skeleton, Tabs, Tag } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getCashContext } from '../../../services/storeCashService'
import type { CashContext } from '../../../types/storeCash'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import StoreSidebarLayout from '../ScheduleAttendance/StoreSidebarLayout'
import { useStoreSidebar } from '../ScheduleAttendance/useStoreSidebar'
import DailyTab from './DailyTab'
import DepositsTab from './DepositsTab'
import ExpensesTab from './ExpensesTab'
import {
  chooseCashErrorText,
  parseCashSearch,
  resolveCashError,
  resolveCashFilters,
  serializeCashQuery,
  STORE_CASH_PATH,
  type CashPageQuery,
  type CashTab,
} from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import OverviewTab from './OverviewTab'
import { LoadErrorAlert, type Tr } from './parts'
import type { CashTabProps } from './tabProps'
import { useCashRequest } from './useCashRequest'
import './storeCash.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）。
registerPageMessages({ zh, en })

function stripQuestionMark(search: string): string {
  return search.startsWith('?') ? search.slice(1) : search
}

/**
 * 现金管理（分店现金池、按日日结、存款、现金支出）：管理员、财务与店长查看统计、为银行对账做准备。
 * - 页签与筛选写进地址栏，刷新、分享可恢复；
 * - 按钮显隐一律以接口返回的 capabilities / canVoid / canReview 为准，不按角色推断；
 * - 日结数据未接入时现金池余额等显示「—」并在页顶说明，不显示成 0。
 */
export default function StoreCashPage() {
  const { t, i18n } = useTranslation()
  // 页面文案都在 storeCash.* 下；tr 只是少写前缀，键名由契约测试静态核对。
  const tr = useCallback<Tr>((key, params) => t(`storeCash.${key}`, params) as string, [t])
  const tf = useCallback<Tr>((key, params) => t(key, params) as string, [t])
  const { active } = useKeepAliveContext()
  const location = useLocation()
  const navigate = useNavigate()

  const [query, setQuery] = useState<CashPageQuery>(() => parseCashSearch(location.search))
  const serialized = serializeCashQuery(query)
  const lastSyncedRef = useRef(stripQuestionMark(location.search))

  // 地址栏 ↔ 页面状态：只在本页激活且地址确实是本页时同步（保活隐藏时全局地址属于别的页面）。
  // 地址栏被外部改动（粘贴链接、前进后退）时以地址为准；否则把当前状态写回地址栏（replace，不堆历史）。
  // 从页签栏切回本页时地址不带参数，也会走写回分支，刷新后仍能恢复。
  useEffect(() => {
    if (!active || location.pathname !== STORE_CASH_PATH) return
    const urlSearch = stripQuestionMark(location.search)
    if (urlSearch === serialized) {
      lastSyncedRef.current = urlSearch
      return
    }
    if (urlSearch && urlSearch !== lastSyncedRef.current) {
      lastSyncedRef.current = urlSearch
      setQuery(parseCashSearch(urlSearch))
      return
    }
    lastSyncedRef.current = serialized
    navigate({ pathname: STORE_CASH_PATH, search: `?${serialized}` }, { replace: true })
  }, [active, location.pathname, location.search, navigate, serialized])

  const updateQuery = useCallback((patch: Partial<CashPageQuery>) => {
    setQuery((current) => ({ ...current, ...patch }))
  }, [])

  const contextRequest = useCashRequest<CashContext>('context', (signal) => getCashContext(signal), active)
  const context = contextRequest.data

  const errorText = useCallback((error: unknown) => {
    const resolved = resolveCashError(error)
    if (resolved.kind === 'abort') return null
    return chooseCashErrorText(resolved, i18n.language, (key) => t(key) as string)
  }, [i18n.language, t])

  // 悉尼「今天」只在 context 还没给出各店今天时兜底；保活页重新激活时刷新，避免跨天后仍用旧日期。
  const fallbackToday = useMemo(() => formatSydneyIsoDate(), [active])
  const filters = useMemo(
    () => resolveCashFilters(query, context?.stores ?? [], fallbackToday),
    [context?.stores, fallbackToday, query],
  )

  const contextError = contextRequest.error ? errorText(contextRequest.error) : null
  const hasStores = Boolean(context && context.stores.length > 0)

  // 左侧分店列表（四个页签共用）。选一家店时同时写入总览的 stores 和按日 / 存款 / 支出共用的 store，
  // 这样切页签看到的是同一家店；「全部分店」只在总览可用，且只清空 stores（其余页签必须按单店看，保留原来的店）。
  const isOverview = query.tab === 'overview'
  const picked = filters.selectedStoreCodes
  const sidebarOptions = useMemo(
    () => filters.storeOptions.map((store) => ({ value: store.storeCode, label: store.storeName || store.storeCode })),
    [filters.storeOptions],
  )
  const selectStore = useCallback((code: string | undefined) => {
    updateQuery(code ? { stores: [code], store: code } : { stores: [] })
  }, [updateQuery])
  // 总览里选了不止一家（老的分享链接可能带多家）：列表里不高亮任何一家，按钮上写明「已选 N 家」
  const multiPicked = isOverview && picked.length > 1
  const storeSidebar = useStoreSidebar({
    options: sidebarOptions,
    value: multiPicked ? '' : isOverview ? picked[0] : filters.storeCode ?? undefined,
    onSelect: selectStore,
    allowAll: isOverview,
    valueLabel: multiPicked ? tr('filters.storesSelected', { count: picked.length }) : undefined,
  })
  // 只有 1 家店（店长）时没有列表，用一枚标签告诉用户当前看的是哪家
  const singleStore = filters.storeOptions.length === 1 ? filters.storeOptions[0] : null

  const tabProps = (tab: CashTab): CashTabProps | null => (context ? {
    context,
    filters,
    query,
    active: active && query.tab === tab,
    tr,
    tf,
    errorText,
    onQueryChange: updateQuery,
  } : null)

  const overviewProps = tabProps('overview')
  const dailyProps = tabProps('daily')
  const depositsProps = tabProps('deposits')
  const expensesProps = tabProps('expenses')

  return (
    <PageContainer title={tr('title')} subtitle={tr('subtitle')}>
      <div className="store-cash-stack">
        {contextError ? <LoadErrorAlert message={contextError} tr={tr} onRetry={contextRequest.reload} /> : null}
        {context && !context.dailyCloseConnected ? (
          <Alert
            type="warning"
            showIcon
            message={tr('dailyCloseMissing.title')}
            description={tr('dailyCloseMissing.description')}
          />
        ) : null}
        {!context && contextRequest.loading ? <Skeleton active paragraph={{ rows: 6 }} /> : null}
        {context && !hasStores ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('noStores')} />
        ) : null}
        {hasStores && overviewProps && dailyProps && depositsProps && expensesProps ? (
          <StoreSidebarLayout docked={storeSidebar.docked} sidebar={storeSidebar.sidebar}>
            {storeSidebar.switcher || singleStore ? (
              <div className="store-cash-store-switch">
                {storeSidebar.switcher ?? (
                  <Tag icon={<ShopOutlined />} className="store-cash-store-single">
                    {singleStore?.storeName || singleStore?.storeCode}
                  </Tag>
                )}
              </div>
            ) : null}
            <Tabs
              className="store-cash-tabs"
              activeKey={query.tab}
              onChange={(key) => updateQuery({ tab: key as CashTab })}
              items={[
                { key: 'overview', label: tr('tabs.overview'), children: <OverviewTab {...overviewProps} /> },
                { key: 'daily', label: tr('tabs.daily'), children: <DailyTab {...dailyProps} /> },
                { key: 'deposits', label: tr('tabs.deposits'), children: <DepositsTab {...depositsProps} /> },
                { key: 'expenses', label: tr('tabs.expenses'), children: <ExpensesTab {...expensesProps} /> },
              ]}
            />
          </StoreSidebarLayout>
        ) : null}
      </div>
    </PageContainer>
  )
}
