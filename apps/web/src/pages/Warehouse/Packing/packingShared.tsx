import { CarOutlined, ShopOutlined } from '@ant-design/icons'
import { Tag, Typography, message } from 'antd'
import type { TFunction } from 'i18next'
import { createContext, useContext, useMemo } from 'react'

import type { StoreOption } from '../../../services/storeService'
import { readPackingError } from '../../../services/warehousePackingService'
import type { PackCarrier, PackDestination, PackEvent } from '../../../types/warehousePacking'
import { PACK_CARTON_STATUS, PACK_DESTINATION_TYPE, PACK_PALLET_STATUS } from '../../../types/warehousePacking'

import { describeDestination, formatPackTime } from './packingLogic'
import { PACK_LABEL_CSS, PACK_LABEL_HEIGHT_PX, PACK_LABEL_WIDTH_PX, buildPackLabelHtml } from './packLabel'
import type { PackLabelFields } from './packLabel'

/** 页面内共享：分店 / 运输公司主数据、打开箱 / 托盘抽屉、数据变更通知。 */
export interface PackingPageContextValue {
  stores: StoreOption[]
  /** 启用中的运输公司（去向选择用）。 */
  carriers: PackCarrier[]
  reloadCarriers: () => void
  openCarton: (cartonGuid: string) => void
  openPallet: (palletGuid: string) => void
  /** 箱 / 托盘有改动时自增，列表据此刷新。 */
  dataVersion: number
  notifyChanged: () => void
}

export const PackingPageContext = createContext<PackingPageContextValue | null>(null)

export function usePackingPage(): PackingPageContextValue {
  const value = useContext(PackingPageContext)
  if (!value) {
    throw new Error('PackingPageContext 未提供')
  }
  return value
}

/** 接口失败统一提示：原样显示后端 message（403「只有仓库经理可以…」等照实提示）。 */
export function showPackingError(error: unknown) {
  void message.error(readPackingError(error).message)
}

export function CartonStatusTag({ status, t }: { status: number; t: TFunction }) {
  if (status === PACK_CARTON_STATUS.Packing) {
    return <Tag color="blue">{t('warehouseUi.packing.status.cartonPacking')}</Tag>
  }
  if (status === PACK_CARTON_STATUS.Sealed) {
    return <Tag color="green">{t('warehouseUi.packing.status.cartonSealed')}</Tag>
  }
  return <Tag>{t('warehouseUi.packing.status.voided')}</Tag>
}

export function PalletStatusTag({ status, t }: { status: number; t: TFunction }) {
  if (status === PACK_PALLET_STATUS.Building) {
    return <Tag color="blue">{t('warehouseUi.packing.status.palletBuilding')}</Tag>
  }
  if (status === PACK_PALLET_STATUS.Sealed) {
    return <Tag color="green">{t('warehouseUi.packing.status.palletSealed')}</Tag>
  }
  return <Tag>{t('warehouseUi.packing.status.voided')}</Tag>
}

export function DestinationText({ destination, t }: { destination?: PackDestination | null; t: TFunction }) {
  const text = describeDestination(destination)
  if (!text) {
    return <Typography.Text type="secondary">{t('warehouseUi.packing.destination.unassigned')}</Typography.Text>
  }
  return (
    <span className="wh-packing-dest">
      {destination?.destinationType === PACK_DESTINATION_TYPE.Carrier ? <CarOutlined /> : <ShopOutlined />}
      {text}
    </span>
  )
}

/** 操作记录的动作名：与后端 WarehousePackEventActions 对应。 */
function eventActionLabel(action: number, t: TFunction): string {
  switch (action) {
    case 1:
      return t('warehouseUi.packing.event.created')
    case 2:
      return t('warehouseUi.packing.event.itemChanged')
    case 3:
      return t('warehouseUi.packing.event.headerChanged')
    case 4:
      return t('warehouseUi.packing.event.sealed')
    case 5:
      return t('warehouseUi.packing.event.reopened')
    case 6:
      return t('warehouseUi.packing.event.voided')
    case 7:
      return t('warehouseUi.packing.event.addedToPallet')
    case 8:
      return t('warehouseUi.packing.event.removedFromPallet')
    case 9:
      return t('warehouseUi.packing.event.locationChanged')
    default:
      return t('warehouseUi.packing.event.other')
  }
}

export function EventList({ events, t }: { events: PackEvent[]; t: TFunction }) {
  if (!events.length) {
    return <Typography.Text type="secondary">{t('warehouseUi.packing.event.empty')}</Typography.Text>
  }
  return (
    <ul className="wh-packing-events">
      {events.map((event, index) => (
        <li key={`${event.createdAtUtc}-${index}`}>
          <span className="wh-packing-events-time">{formatPackTime(event.createdAtUtc)}</span>
          <span className="wh-packing-events-action">{eventActionLabel(event.action, t)}</span>
          {event.detail ? <span className="wh-packing-events-detail">{event.detail}</span> : null}
          <span className="wh-packing-events-operator">{event.operatorName}</span>
        </li>
      ))}
    </ul>
  )
}

/** 标签预览：与打印用同一份 HTML 和样式，按比例缩小显示。 */
export function PackLabelPreview({ fields, scale = 0.6 }: { fields: PackLabelFields; scale?: number }) {
  const html = useMemo(() => buildPackLabelHtml(fields), [fields])
  return (
    <div
      className="wh-packing-label-preview"
      style={{ width: PACK_LABEL_WIDTH_PX * scale, height: PACK_LABEL_HEIGHT_PX * scale }}
    >
      <style>{PACK_LABEL_CSS}</style>
      {/* HTML 由 buildPackLabelHtml 生成，文字已转义。 */}
      <div style={{ transform: `scale(${scale})`, transformOrigin: '0 0' }} dangerouslySetInnerHTML={{ __html: html }} />
    </div>
  )
}
