import { ShopOutlined } from '@ant-design/icons'
import { Alert, Badge, Result, Select, Spin, Tabs } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import PageContainer from '../../components/PageContainer'
import { registerPageMessages } from '../../i18n/registerPageMessages'
import {
  getPackScope,
  listPackCarriers,
  listPackDestinations,
  listPackIncoming,
  listPackLocations,
  readPackingError,
} from '../../services/packingService'
import type { PackCarrier, PackIncoming, PackLocation, PackOwner, PackScope } from '../../types/packing'

import CarriersTab from './CarriersTab'
import CartonDrawer from './CartonDrawer'
import CartonsTab from './CartonsTab'
import LocateTab from './LocateTab'
import LocationsTab from './LocationsTab'
import { PACK_OWNER_STORAGE_KEY, resolveInitialOwnerCode } from './packingLogic'
import packingMessagesEn from './packingMessages.en.json'
import packingMessagesZh from './packingMessages.zh.json'
import { PackingPageContext, showPackingError } from './packingShared'
import type { PackingPageContextValue } from './packingShared'
import PalletDrawer from './PalletDrawer'
import PalletsTab from './PalletsTab'
import ReceiveTab from './ReceiveTab'
import StationTab from './StationTab'
import './packing.css'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: packingMessagesZh, en: packingMessagesEn })

type PackingTabKey = 'station' | 'cartons' | 'pallets' | 'receive' | 'locate' | 'locations' | 'carriers'

const EMPTY_INCOMING: PackIncoming = { pending: [], recent: [] }

function readSavedOwner(): string | null {
  try {
    return window.localStorage.getItem(PACK_OWNER_STORAGE_KEY)
  } catch {
    return null
  }
}

function saveOwner(code: string) {
  try {
    window.localStorage.setItem(PACK_OWNER_STORAGE_KEY, code)
  } catch {
    // 本机存储不可用时只是不记忆选择。
  }
}

interface WorkspaceProps {
  scope: PackScope
  ownerCode: string
}

/** 选定地点后的工作区：切换地点时整体重建，所有标签页按新地点重新加载。 */
function PackingWorkspace({ scope, ownerCode }: WorkspaceProps) {
  const { t } = useTranslation()
  const { active: pageActive } = useKeepAliveContext()
  const owner = scope.owners.find((item) => item.code === ownerCode) ?? scope.owners[0]
  const [tab, setTab] = useState<PackingTabKey>('station')
  const [destinations, setDestinations] = useState<PackOwner[]>([])
  const [storesError, setStoresError] = useState<string | null>(null)
  const [carriers, setCarriers] = useState<PackCarrier[]>([])
  const [locations, setLocations] = useState<PackLocation[]>([])
  const [incoming, setIncoming] = useState<PackIncoming>(EMPTY_INCOMING)
  const [cartonGuid, setCartonGuid] = useState<string | null>(null)
  const [palletGuid, setPalletGuid] = useState<string | null>(null)
  const [dataVersion, setDataVersion] = useState(0)

  const reloadCarriers = useCallback(() => {
    listPackCarriers()
      .then(setCarriers)
      .catch(showPackingError)
  }, [])

  const reloadLocations = useCallback(() => {
    listPackLocations(ownerCode)
      .then(setLocations)
      .catch(showPackingError)
  }, [ownerCode])

  const reloadIncoming = useCallback(() => {
    listPackIncoming(ownerCode)
      .then(setIncoming)
      .catch(showPackingError)
  }, [ownerCode])

  useEffect(() => {
    // 去向候选（其他分店 + 总仓）：走打包自己的接口，店员没有「查看分店」权限也能用。
    listPackDestinations(ownerCode)
      .then((list) => {
        setDestinations(list)
        setStoresError(null)
      })
      .catch((error) => setStoresError(t('packingUi.destination.storesError', { reason: readPackingError(error).message })))
    reloadCarriers()
    reloadLocations()
    // 只在挂载（切换地点重建）时加载一次主数据。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    reloadIncoming()
  }, [dataVersion, reloadIncoming])

  const notifyChanged = useCallback(() => setDataVersion((version) => version + 1), [])
  const stores = useMemo(
    () => destinations.filter((place) => !place.isWarehouse).map((place) => ({ value: place.code, label: place.name })),
    [destinations],
  )
  const warehouseSelectable = useMemo(() => destinations.some((place) => place.isWarehouse), [destinations])

  const contextValue = useMemo<PackingPageContextValue>(
    () => ({
      owner,
      ownerCode,
      isWarehouse: owner.isWarehouse,
      isManager: scope.isManager,
      isAdmin: scope.isAdmin,
      stores,
      warehouseSelectable,
      storesError,
      carriers,
      reloadCarriers,
      locations,
      reloadLocations,
      incoming,
      reloadIncoming,
      openCarton: setCartonGuid,
      openPallet: setPalletGuid,
      dataVersion,
      notifyChanged,
    }),
    [carriers, dataVersion, incoming, locations, notifyChanged, owner, ownerCode, reloadCarriers, reloadIncoming, reloadLocations, scope, stores, storesError, warehouseSelectable],
  )

  // 页面被 KeepAlive 隐藏、切到别的标签或开着详情抽屉时，对应标签不监听扫码枪和快捷键。
  const drawerOpen = cartonGuid !== null || palletGuid !== null
  const isActive = (key: PackingTabKey) => pageActive && tab === key

  return (
    <PackingPageContext.Provider value={contextValue}>
      <Tabs
        className="wh-packing-tabs"
        activeKey={tab}
        onChange={(key) => setTab(key as PackingTabKey)}
        items={[
          {
            key: 'station',
            label: t('packingUi.tab.station'),
            children: <StationTab active={isActive('station') && !drawerOpen} />,
          },
          {
            key: 'cartons',
            label: t('packingUi.tab.cartons'),
            children: <CartonsTab active={isActive('cartons')} />,
          },
          {
            key: 'pallets',
            label: t('packingUi.tab.pallets'),
            children: <PalletsTab active={isActive('pallets')} />,
          },
          {
            key: 'receive',
            label: (
              <span>
                {t('packingUi.tab.receive')}
                {incoming.pending.length > 0 ? <Badge className="wh-packing-tab-badge" count={incoming.pending.length} size="small" /> : null}
              </span>
            ),
            children: <ReceiveTab active={isActive('receive') && !drawerOpen} />,
          },
          {
            key: 'locate',
            label: t('packingUi.tab.locate'),
            children: <LocateTab active={isActive('locate') && !drawerOpen} />,
          },
          {
            key: 'locations',
            label: t('packingUi.tab.locations'),
            children: <LocationsTab active={isActive('locations')} />,
          },
          {
            key: 'carriers',
            label: t('packingUi.tab.carriers'),
            children: <CarriersTab active={isActive('carriers')} />,
          },
        ]}
      />
      <PalletDrawer palletGuid={palletGuid} onClose={() => setPalletGuid(null)} />
      <CartonDrawer cartonGuid={cartonGuid} onClose={() => setCartonGuid(null)} />
    </PackingPageContext.Provider>
  )
}

/** 扫码打包：打包台 / 箱 / 托盘 / 到货签收 / 商品位置 / 存放位置 / 运输公司；分店和总仓共用，右上角选地点。 */
export default function PackingPage() {
  const { t } = useTranslation()
  const [scope, setScope] = useState<PackScope | null>(null)
  const [scopeError, setScopeError] = useState<{ status: number; message: string } | null>(null)
  const [ownerCode, setOwnerCode] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    getPackScope()
      .then((result) => {
        if (cancelled) {
          return
        }
        setScope(result)
        setOwnerCode(resolveInitialOwnerCode(result.owners, readSavedOwner()))
      })
      .catch((error) => {
        if (!cancelled) {
          const info = readPackingError(error)
          setScopeError({ status: info.status, message: info.message })
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  const changeOwner = (code: string) => {
    setOwnerCode(code)
    saveOwner(code)
  }

  // 右上角：只有一个地点时显示成只读文字，多个时可切换（店长管多家店、管理员有全部分店和总仓）。
  const ownerPicker =
    scope && ownerCode ? (
      scope.owners.length > 1 ? (
        <Select
          className="wh-packing-owner-select"
          showSearch
          optionFilterProp="label"
          value={ownerCode}
          onChange={changeOwner}
          suffixIcon={<ShopOutlined />}
          options={scope.owners.map((owner) => ({ value: owner.code, label: owner.name }))}
          aria-label={t('packingUi.owner.label')}
        />
      ) : (
        <span className="wh-packing-owner-text">
          <ShopOutlined /> {scope.owners[0].name}
        </span>
      )
    ) : null

  return (
    <PageContainer title={t('menu.packing')} subtitle={t('packingUi.subtitle')} compact extra={ownerPicker}>
      {scopeError ? (
        <Result status={scopeError.status === 403 ? '403' : 'error'} title={scopeError.message} />
      ) : !scope || !ownerCode ? (
        <div className="wh-packing-loading">
          <Spin />
        </div>
      ) : scope.owners.length === 0 ? (
        <Alert type="warning" showIcon message={t('packingUi.owner.none')} />
      ) : (
        <PackingWorkspace key={ownerCode} scope={scope} ownerCode={ownerCode} />
      )}
    </PageContainer>
  )
}
