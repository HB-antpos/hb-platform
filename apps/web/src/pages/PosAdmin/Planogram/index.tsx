import { Tabs } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { listAllHookModels } from '../../../services/planogramService'
import { useAuthStore } from '../../../store/auth'
import type { HookModelDto } from '../../../types/planogram'
import ModelsTab from './Models/ModelsTab'
import PlanList from './PlanList/PlanList'
import en from './planogramMessages.en.json'
import zh from './planogramMessages.zh.json'
import {
  parsePlanogramSearch,
  PLANOGRAM_PATH,
  serializePlanogramQuery,
  type ModelView,
  type PlanogramPageQuery,
  type PlanogramTab,
} from './planogramLogic'
import ProductSpecs from './ProductSpecs/ProductSpecs'
import { usePlanogramRequest } from './usePlanogramRequest'
import './planogram.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）。
registerPageMessages({ zh, en })

function stripQuestionMark(search: string): string {
  return search.startsWith('?') ? search.slice(1) : search
}

const NO_HOOKS: HookModelDto[] = []

/**
 * 商品陈列图：陈列方案（默认、排在最前）+ 商品陈列参数 + 货架与挂钩型号，三个页签。
 * - 页签与型号子页签写进地址栏（replace，不堆历史），刷新、分享可恢复；陈列方案是默认页签，不写进地址栏；
 * - 按钮显隐按权限：编辑类看 canEditPlanogram，型号维护 / 方案归档删除看 canManagePlanogram，只有查看权限时抽屉只读；
 * - 挂钩型号目录（名称、臂长、承重）由本页统一加载，商品参数列表的「默认挂钩 / 每钩挂数」与抽屉的挂数表都用它；
 * - 方案「打开」进入设计器子页面 /pos-admin/planograms/:guid/design（独立懒加载页，每个方案一个页签）；从设计器回到本页时刷新方案列表。
 */
export default function PlanogramPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const { active } = useKeepAliveContext()
  const location = useLocation()
  const navigate = useNavigate()

  const [query, setQuery] = useState<PlanogramPageQuery>(() => parsePlanogramSearch(location.search))
  const serialized = serializePlanogramQuery(query)
  const lastSyncedRef = useRef(stripQuestionMark(location.search))

  // 地址栏 ↔ 页面状态：只在本页激活且地址确实是本页时同步（保活隐藏时全局地址属于别的页面）。
  // 地址栏被外部改动（粘贴链接、前进后退）时以地址为准；否则把当前状态写回地址栏（replace）。
  useEffect(() => {
    if (!active || location.pathname !== PLANOGRAM_PATH) return
    const urlSearch = stripQuestionMark(location.search)
    if (urlSearch === serialized) {
      lastSyncedRef.current = urlSearch
      return
    }
    if (urlSearch && urlSearch !== lastSyncedRef.current) {
      lastSyncedRef.current = urlSearch
      setQuery(parsePlanogramSearch(urlSearch))
      return
    }
    lastSyncedRef.current = serialized
    navigate({ pathname: PLANOGRAM_PATH, search: serialized ? `?${serialized}` : '' }, { replace: true })
  }, [active, location.pathname, location.search, navigate, serialized])

  // 从设计器（别的页签）回到本页：本页重新激活时让方案列表刷新一次，保存后的版本 / 统计 / 冲突数才是最新的
  const [refreshSignal, setRefreshSignal] = useState(0)
  const wasActiveRef = useRef(active)
  useEffect(() => {
    if (active && !wasActiveRef.current) setRefreshSignal((value) => value + 1)
    wasActiveRef.current = active
  }, [active])

  // 切到「陈列方案」页签时同样触发一次刷新：基础资料（货架型号等）可能刚在别的页签里改过
  const setTab = useCallback((tab: PlanogramTab) => {
    setQuery((current) => ({ ...current, tab }))
    if (tab === 'plans') setRefreshSignal((value) => value + 1)
  }, [])
  const setView = useCallback((view: ModelView) => setQuery((current) => ({ ...current, view })), [])

  // 挂钩型号目录：换 key 即重新取（保存 / 启停 / 抽屉打开时都会换），取的过程中保留上一份数据
  const [hooksVersion, setHooksVersion] = useState(0)
  const hooksRequest = usePlanogramRequest<HookModelDto[]>(`hooks:${hooksVersion}`, (signal) => listAllHookModels(signal))
  const reloadHooks = useCallback(() => setHooksVersion((value) => value + 1), [])
  const hooks = hooksRequest.data ?? NO_HOOKS
  const hooksReady = hooksRequest.data !== null && !hooksRequest.error
  const hooksFailed = Boolean(hooksRequest.error)

  return (
    <PageContainer title={t('planogram.title')} subtitle={t('planogram.subtitle')}>
      <div className="planogram-card">
        <Tabs
          className="planogram-tabs"
          activeKey={query.tab}
          onChange={(key) => setTab(key as PlanogramTab)}
          items={[
            {
              key: 'plans',
              label: t('planogram.tabs.plans'),
              children: <PlanList canView={access.canViewPlanogram} canEdit={access.canEditPlanogram} canManage={access.canManagePlanogram} refreshSignal={refreshSignal} />,
            },
            {
              key: 'specs',
              label: t('planogram.tabs.specs'),
              children: (
                <ProductSpecs
                  canEdit={access.canEditPlanogram}
                  canManage={access.canManagePlanogram}
                  hooks={hooks}
                  hooksReady={hooksReady}
                  hooksFailed={hooksFailed}
                  onReloadHooks={reloadHooks}
                />
              ),
            },
            {
              key: 'models',
              label: t('planogram.tabs.models'),
              children: (
                <ModelsTab
                  view={query.view}
                  onViewChange={setView}
                  canManage={access.canManagePlanogram}
                  onHooksChanged={reloadHooks}
                />
              ),
            },
          ]}
        />
      </div>
    </PageContainer>
  )
}
