import { Alert, Button, Result, Spin } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useDynamicTabTitle } from '../../../../hooks/useDynamicTabTitle'
import { useStableRouteContext } from '../../../../hooks/useStableRouteContext'
import { registerPageMessages } from '../../../../i18n/registerPageMessages'
import { getPlan } from '../../../../services/planogramService'
import { useAuthStore } from '../../../../store/auth'
import type { PlanogramDetailDto } from '../../../../types/planogram'
import en from '../planogramMessages.en.json'
import zh from '../planogramMessages.zh.json'
import { PLANOGRAM_PATH, planogramErrorText, resolvePlanogramError } from '../planogramLogic'
import { usePlanogramRequest } from '../usePlanogramRequest'
import DesignerWorkspace from './DesignerWorkspace'
import { createDesignerStore, DesignerStoreProvider, useDesignerStore, type DesignerStoreApi } from './useDesignerStore'
import '../planogram.css'
import './designer.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包；设计器可能被直接深链打开（列表页代码块还没加载），所以这里也要注册（重复注册幂等）。
registerPageMessages({ zh, en })

/** 页签标题跟随方案名（保存后的名字）。放在 store 的 Provider 里才能读到名称。 */
function TabTitle() {
  const { t } = useTranslation()
  const name = useDesignerStore((state) => state.header.name)
  useDynamicTabTitle(name ? t('planogram.designer.tabTitle', { name }) : undefined)
  return null
}

/**
 * 陈列方案设计器（路由 /pos-admin/planograms/:guid/design，不进菜单；从方案列表「打开」进入）。
 * - 方案 guid 取自保活路由上下文（useStableRouteContext，保活隐藏后全局地址变化不会把参数清空或换成别人的）；
 * - 载入成功后创建本页专属的 store（保活的多个方案互不干扰）；「重新载入」（并发冲突后）得到新数据 → 新 store；
 * - 编辑权限看 canEditPlanogram：没有编辑权限、已归档或窄屏时设计器只读。
 */
export default function PlanogramDesignerPage() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const route = useStableRouteContext()
  const { active } = useKeepAliveContext()
  const access = useAuthStore((state) => state.access)
  const guid = route?.params.guid ?? ''
  const [nonce, setNonce] = useState(0)
  const request = usePlanogramRequest<PlanogramDetailDto>(guid ? `plan:${guid}:${nonce}` : null, (signal) => getPlan(guid, signal))

  const store = useMemo<DesignerStoreApi | null>(() => (request.data ? createDesignerStore(request.data) : null), [request.data])

  if (!guid) {
    return <Result status="404" title={t('planogram.designer.notFound')} extra={<Button onClick={() => navigate(PLANOGRAM_PATH)}>{t('planogram.designer.backToList')}</Button>} />
  }

  if (store) {
    return (
      <DesignerStoreProvider store={store}>
        <TabTitle />
        <DesignerWorkspace canEdit={access.canEditPlanogram} canManage={access.canManagePlanogram} canView={access.canViewPlanogram} onReload={() => setNonce((value) => value + 1)} />
      </DesignerStoreProvider>
    )
  }

  if (request.error) {
    const resolved = resolvePlanogramError(request.error)
    const notFound = resolved.kind === 'error' && resolved.code === 'PLANOGRAM_NOT_FOUND'
    const text = resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : ''
    return (
      <div className="planogram-designer-state">
        <Alert
          type="error"
          showIcon
          message={notFound ? t('planogram.designer.notFound') : text}
          action={
            <>
              {notFound ? null : <Button size="small" onClick={request.reload}>{t('planogram.common.retry')}</Button>}
              <Button size="small" type="link" onClick={() => navigate(PLANOGRAM_PATH)}>{t('planogram.designer.backToList')}</Button>
            </>
          }
        />
      </div>
    )
  }

  return (
    <div className="planogram-designer-state is-center" aria-busy={active}>
      <Spin tip={t('planogram.designer.loading')}><div style={{ minHeight: 120, minWidth: 200 }} /></Spin>
    </div>
  )
}
