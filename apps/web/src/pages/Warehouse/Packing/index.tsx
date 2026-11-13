import { Tabs } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveStores } from '../../../services/storeService'
import type { StoreOption } from '../../../services/storeService'
import { listPackCarriers } from '../../../services/warehousePackingService'
import type { PackCarrier } from '../../../types/warehousePacking'

import CarriersTab from './CarriersTab'
import CartonDrawer from './CartonDrawer'
import CartonsTab from './CartonsTab'
import LocateTab from './LocateTab'
import packingMessagesEn from './packingMessages.en.json'
import packingMessagesZh from './packingMessages.zh.json'
import { PackingPageContext, showPackingError } from './packingShared'
import type { PackingPageContextValue } from './packingShared'
import PalletDrawer from './PalletDrawer'
import PalletsTab from './PalletsTab'
import StationTab from './StationTab'
import './packing.css'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: packingMessagesZh, en: packingMessagesEn })

type PackingTabKey = 'station' | 'cartons' | 'pallets' | 'locate' | 'carriers'

/** 扫码打包：打包台 / 箱 / 托盘 / 商品位置 / 运输公司（发运单与分店签收属于二期，不在本页）。 */
export default function WarehousePackingPage() {
  const { t } = useTranslation()
  const { active: pageActive } = useKeepAliveContext()
  const [tab, setTab] = useState<PackingTabKey>('station')
  const [stores, setStores] = useState<StoreOption[]>([])
  const [carriers, setCarriers] = useState<PackCarrier[]>([])
  const [cartonGuid, setCartonGuid] = useState<string | null>(null)
  const [palletGuid, setPalletGuid] = useState<string | null>(null)
  const [dataVersion, setDataVersion] = useState(0)

  const reloadCarriers = useCallback(() => {
    listPackCarriers()
      .then(setCarriers)
      .catch(showPackingError)
  }, [])

  useEffect(() => {
    getActiveStores()
      .then(setStores)
      .catch(showPackingError)
    reloadCarriers()
  }, [reloadCarriers])

  const notifyChanged = useCallback(() => setDataVersion((version) => version + 1), [])

  const contextValue = useMemo<PackingPageContextValue>(
    () => ({
      stores,
      carriers,
      reloadCarriers,
      openCarton: setCartonGuid,
      openPallet: setPalletGuid,
      dataVersion,
      notifyChanged,
    }),
    [carriers, dataVersion, notifyChanged, reloadCarriers, stores],
  )

  // 页面被 KeepAlive 隐藏、切到别的标签或开着详情抽屉时，对应标签不监听扫码枪和快捷键。
  const drawerOpen = cartonGuid !== null || palletGuid !== null
  const isActive = (key: PackingTabKey) => pageActive && tab === key

  return (
    <PackingPageContext.Provider value={contextValue}>
      <PageContainer title={t('menu.warehousePacking')} subtitle={t('warehouseUi.packing.subtitle')} compact>
        <Tabs
          className="wh-packing-tabs"
          activeKey={tab}
          onChange={(key) => setTab(key as PackingTabKey)}
          items={[
            {
              key: 'station',
              label: t('warehouseUi.packing.tab.station'),
              children: <StationTab active={isActive('station') && !drawerOpen} />,
            },
            {
              key: 'cartons',
              label: t('warehouseUi.packing.tab.cartons'),
              children: <CartonsTab active={isActive('cartons')} />,
            },
            {
              key: 'pallets',
              label: t('warehouseUi.packing.tab.pallets'),
              children: <PalletsTab active={isActive('pallets')} />,
            },
            {
              key: 'locate',
              label: t('warehouseUi.packing.tab.locate'),
              children: <LocateTab active={isActive('locate') && !drawerOpen} />,
            },
            {
              key: 'carriers',
              label: t('warehouseUi.packing.tab.carriers'),
              children: <CarriersTab active={isActive('carriers')} />,
            },
          ]}
        />
        <PalletDrawer palletGuid={palletGuid} onClose={() => setPalletGuid(null)} />
        <CartonDrawer cartonGuid={cartonGuid} onClose={() => setCartonGuid(null)} />
      </PageContainer>
    </PackingPageContext.Provider>
  )
}
