import { CarOutlined, EnvironmentOutlined, ShopOutlined } from '@ant-design/icons'
import { Tag, Typography, message } from 'antd'
import type { TFunction } from 'i18next'
import { createContext, useContext, useMemo } from 'react'

import { readPackingError } from '../../services/packingService'
import type {
  PackCarrier,
  PackDestination,
  PackEvent,
  PackIncoming,
  PackLocation,
  PackOwner,
} from '../../types/packing'

import { destinationKind, destinationPlaceName, formatPackTime } from './packingLogic'
import type { PackStateKind } from './packingLogic'
import { PACK_LABEL_CSS, PACK_LABEL_HEIGHT_PX, PACK_LABEL_WIDTH_PX, buildLocationLabelHtml, buildPackLabelHtml } from './packLabel'
import type { LocationLabelFields, PackLabelFields } from './packLabel'

/** 页面内共享：当前地点、账号身份、分店 / 运输公司 / 存放位置主数据、打开箱 / 托盘抽屉、数据变更通知。 */
export interface PackingPageContextValue {
  owner: PackOwner
  ownerCode: string
  isWarehouse: boolean
  /** 店长 / 仓库经理 / 管理员：才能重新开箱 / 开托、维护存放位置、查看全部分店。 */
  isManager: boolean
  /** 管理员：才能修改 / 停用运输公司。 */
  isAdmin: boolean
  /** 可选作去向的其他分店（不含当前地点）；来自 packing/destinations 接口，加载失败时为空。 */
  stores: { value: string; label: string }[]
  /** 「总仓」是否可选：发出地点是总仓时为 false；候选加载失败时也为 false。 */
  warehouseSelectable: boolean
  storesError: string | null
  /** 启用中的运输公司（「怎么送」选择用）。 */
  carriers: PackCarrier[]
  reloadCarriers: () => void
  /** 当前地点启用中的存放位置。 */
  locations: PackLocation[]
  reloadLocations: () => void
  /** 待签收 / 最近签收（到货签收页与标签页角标共用）。 */
  incoming: PackIncoming
  reloadIncoming: () => void
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

/** 接口失败统一提示：原样显示后端 message（403「只有店长可以…」等照实提示）。 */
export function showPackingError(error: unknown) {
  void message.error(readPackingError(error).message)
}

/** 状态标签：按分店场景（装箱中、本地存放、在路上、对方已签收）。 */
export function StateTag({ kind, t }: { kind: PackStateKind; t: TFunction }) {
  switch (kind) {
    case 'packing':
      return <Tag color="blue">{t('packingUi.status.packing')}</Tag>
    case 'building':
      return <Tag color="blue">{t('packingUi.status.building')}</Tag>
    case 'stored':
      return <Tag color="green">{t('packingUi.status.stored')}</Tag>
    case 'transit':
      return <Tag color="purple">{t('packingUi.status.transit')}</Tag>
    case 'received':
      return <Tag>{t('packingUi.status.received')}</Tag>
    default:
      return <Tag>{t('packingUi.status.voided')}</Tag>
  }
}

/** 去向文字：其他分店 / 总仓显示名称，有运输公司再加「· 运输公司」；本地存放、未指定显示对应说明。 */
export function DestinationText({ destination, t }: { destination?: PackDestination | null; t: TFunction }) {
  const kind = destinationKind(destination)
  if (kind === 'unassigned') {
    return <Typography.Text type="secondary">{t('packingUi.destination.unassigned')}</Typography.Text>
  }
  if (kind === 'local') {
    return (
      <span className="wh-packing-dest">
        <EnvironmentOutlined />
        {t('packingUi.destination.local')}
      </span>
    )
  }
  const place = destinationPlaceName(destination) ?? ''
  const carrier = destination?.carrierName
  return (
    <span className="wh-packing-dest">
      <ShopOutlined />
      {place}
      {carrier ? (
        <Typography.Text type="secondary" className="wh-packing-dest-via">
          <CarOutlined /> {carrier}
        </Typography.Text>
      ) : null}
    </span>
  )
}

/** 对方已签收的箱 / 托盘：去向字段已被重置为本地存放，这里改显示现在归谁（接收方）。 */
export function ReceivedPlace({ ownerName }: { ownerName: string }) {
  return (
    <span className="wh-packing-dest">
      <ShopOutlined />
      {ownerName}
    </span>
  )
}

/** 操作记录的动作名：与后端 PackEventActions 对应。 */
function eventActionLabel(action: number, t: TFunction): string {
  switch (action) {
    case 1:
      return t('packingUi.event.created')
    case 2:
      return t('packingUi.event.itemChanged')
    case 3:
      return t('packingUi.event.headerChanged')
    case 4:
      return t('packingUi.event.sealed')
    case 5:
      return t('packingUi.event.reopened')
    case 6:
      return t('packingUi.event.voided')
    case 7:
      return t('packingUi.event.addedToPallet')
    case 8:
      return t('packingUi.event.removedFromPallet')
    case 9:
      return t('packingUi.event.locationChanged')
    case 10:
      return t('packingUi.event.dispatched')
    case 11:
      return t('packingUi.event.received')
    default:
      return t('packingUi.event.other')
  }
}

export function EventList({ events, t }: { events: PackEvent[]; t: TFunction }) {
  if (!events.length) {
    return <Typography.Text type="secondary">{t('packingUi.event.empty')}</Typography.Text>
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

function LabelFrame({ html, scale }: { html: string; scale: number }) {
  return (
    <div className="wh-packing-label-preview" style={{ width: PACK_LABEL_WIDTH_PX * scale, height: PACK_LABEL_HEIGHT_PX * scale }}>
      <style>{PACK_LABEL_CSS}</style>
      {/* HTML 由 packLabel 生成，文字已转义。 */}
      <div style={{ transform: `scale(${scale})`, transformOrigin: '0 0' }} dangerouslySetInnerHTML={{ __html: html }} />
    </div>
  )
}

/** 箱 / 托盘标签预览：与打印用同一份 HTML 和样式，按比例缩小显示。 */
export function PackLabelPreview({ fields, scale = 0.6 }: { fields: PackLabelFields; scale?: number }) {
  const html = useMemo(() => buildPackLabelHtml(fields), [fields])
  return <LabelFrame html={html} scale={scale} />
}

/** 存放位置标签预览。 */
export function LocationLabelPreview({ fields, scale = 0.6 }: { fields: LocationLabelFields; scale?: number }) {
  const html = useMemo(() => buildLocationLabelHtml(fields), [fields])
  return <LabelFrame html={html} scale={scale} />
}
