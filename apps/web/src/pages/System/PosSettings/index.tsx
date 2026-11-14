import { HistoryOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Skeleton, Tabs } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getPosSettingsCatalog,
  getPosSettingsGlobal,
  listPosSettingStores,
} from '../../../services/posSettingsService'
import { useAuthStore } from '../../../store/auth'
import ChangesModal from './ChangesModal'
import DevicesTab from './DevicesTab'
import GlobalStoresTab from './GlobalStoresTab'
import { choosePosSettingsErrorText, resolvePosSettingsError } from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import { LoadErrorAlert, type Tr } from './parts'
import StoreDrawer from './StoreDrawer'
import { useRequest } from './useRequest'
import './posSettings.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）。
registerPageMessages({ zh, en })

type PosSettingsTab = 'global' | 'devices'

const EMPTY_VALUES: Readonly<Record<string, boolean>> = {}

/**
 * 收银端设置：总部下发到收银机的业务开关。三层（全局 → 门店 → 设备）越具体越优先，每一层每一项都是三态：
 * 未设置 / 跟随上一层、开、关。
 * - 只有 PosSettings.Manage 才能编辑；只有 View 时三态控件只读、不显示保存按钮；
 * - 全局层的改动只有管理员能保存，非管理员后端返回 403，页面直接显示后端 message；
 * - 设置项清单以接口返回的 catalog 为准，页面不写死清单；
 * - 保存前一律先确认「旧 → 新」，保存成功后刷新数据。
 */
export default function PosSettingsPage() {
  const { t, i18n } = useTranslation()
  // 页面文案都在 posSettings.* 下；tr 只是少写前缀，键名由契约测试静态核对。
  const tr = useCallback<Tr>((key, params) => t(`posSettings.${key}`, params) as string, [t])
  const { active } = useKeepAliveContext()
  const canManage = useAuthStore((state) => state.access.canManagePosSettings)
  const [tab, setTab] = useState<PosSettingsTab>('global')
  const [storeTarget, setStoreTarget] = useState<{ storeCode: string; seq: number } | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [historySeq, setHistorySeq] = useState(0)
  const seqRef = useRef(0)

  const errorText = useCallback((error: unknown) => {
    const resolved = resolvePosSettingsError(error)
    if (resolved.kind === 'abort') return null
    return choosePosSettingsErrorText(resolved, i18n.language, (key) => t(key) as string)
  }, [i18n.language, t])

  const catalogRequest = useRequest('catalog', (signal) => getPosSettingsCatalog(signal), active)
  const globalRequest = useRequest('global', (signal) => getPosSettingsGlobal(signal), active)
  const storesRequest = useRequest('stores', (signal) => listPosSettingStores(signal), active)

  // definitions 必须稳定引用：下游草稿 hook 以它为依赖，每次渲染换新数组会反复重置草稿。
  const catalogData = catalogRequest.data
  const definitions = useMemo(() => catalogData?.definitions ?? [], [catalogData])
  const globalLayer = globalRequest.data
  const stores = useMemo(() => storesRequest.data ?? [], [storesRequest.data])
  const storeNames = useMemo(
    () => Object.fromEntries(stores.map((store) => [store.storeCode, store.storeName])),
    [stores],
  )

  const firstError = catalogRequest.error ?? globalRequest.error ?? storesRequest.error
  const errorMessage = firstError ? errorText(firstError) : null
  const reloadAll = () => {
    catalogRequest.reload()
    globalRequest.reload()
    storesRequest.reload()
  }
  const ready = Boolean(catalogData && globalLayer && storesRequest.data)
  const loading = catalogRequest.loading || globalRequest.loading || storesRequest.loading

  const openStore = (storeCode: string) => {
    seqRef.current += 1
    setStoreTarget({ storeCode, seq: seqRef.current })
  }

  return (
    <PageContainer
      title={tr('title')}
      subtitle={tr('subtitle')}
      extra={(
        <Button
          icon={<HistoryOutlined />}
          onClick={() => {
            setHistorySeq((value) => value + 1)
            setHistoryOpen(true)
          }}
        >
          {tr('historyButton')}
        </Button>
      )}
    >
      <div className="pos-settings-stack">
        {errorMessage ? <LoadErrorAlert message={errorMessage} tr={tr} onRetry={reloadAll} /> : null}
        {!canManage ? <Alert type="info" showIcon message={tr('readOnlyNote')} /> : null}
        {!ready && loading ? <Skeleton active paragraph={{ rows: 8 }} /> : null}
        {ready && catalogData && globalLayer ? (
          definitions.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('emptyCatalog')} />
          ) : (
            <Tabs
              className="pos-settings-tabs"
              activeKey={tab}
              onChange={(key) => setTab(key as PosSettingsTab)}
              items={[
                {
                  key: 'global',
                  label: tr('tabs.global'),
                  children: (
                    <GlobalStoresTab
                      definitions={definitions}
                      global={globalLayer}
                      stores={stores}
                      canManage={canManage}
                      tr={tr}
                      errorText={errorText}
                      onOpenStore={openStore}
                      onChanged={reloadAll}
                    />
                  ),
                },
                {
                  key: 'devices',
                  label: tr('tabs.devices'),
                  children: (
                    <DevicesTab
                      definitions={definitions}
                      globalValues={globalLayer.values}
                      stores={stores}
                      canManage={canManage}
                      active={active && tab === 'devices'}
                      tr={tr}
                      errorText={errorText}
                      onChanged={storesRequest.reload}
                    />
                  ),
                },
              ]}
            />
          )
        ) : null}
      </div>
      <StoreDrawer
        target={storeTarget}
        store={stores.find((store) => store.storeCode === storeTarget?.storeCode) ?? null}
        definitions={definitions}
        globalValues={globalLayer?.values ?? EMPTY_VALUES}
        canManage={canManage}
        active={active}
        tr={tr}
        errorText={errorText}
        onClose={() => setStoreTarget(null)}
        onChanged={storesRequest.reload}
      />
      <ChangesModal
        open={historyOpen}
        seq={historySeq}
        storeNames={storeNames}
        tr={tr}
        errorText={errorText}
        onClose={() => setHistoryOpen(false)}
      />
    </PageContainer>
  )
}
