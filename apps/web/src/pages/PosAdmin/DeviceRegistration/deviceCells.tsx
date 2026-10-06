import { Tag, Tooltip } from 'antd'
import type { TFunction } from 'i18next'

import { getDeviceStatusTone, getRelativeTimeParts } from './deviceManagementLogic'

export const EMPTY_VALUE = '--'

const DEVICE_TYPE_COLOR_MAP: Record<string, string> = {
  mobile: 'blue',
  pda: 'purple',
  storepda: 'purple',
  warehousepda: 'cyan',
  pos: 'volcano',
  admin: 'gold',
}

export function formatDateTime(value?: string | null) {
  if (!value) {
    return EMPTY_VALUE
  }
  const timestamp = Date.parse(value)
  return Number.isNaN(timestamp) ? value : new Date(timestamp).toLocaleString()
}

export function formatRelativeTime(value: string | null | undefined, t: TFunction, now = Date.now()) {
  const parts = getRelativeTimeParts(value, now)
  if (!parts) {
    return null
  }
  if (parts.unit === 'justNow') {
    return t('posAdmin.devices.mgmt.relative.justNow')
  }
  if (parts.unit === 'date') {
    return parts.date.toLocaleDateString()
  }
  return t(`posAdmin.devices.mgmt.relative.${parts.unit}`, { count: parts.count })
}

/** 相对时间 + Tooltip 完整时间；空值显示占位。 */
export function RelativeTime({ value, t, empty }: { value?: string | null; t: TFunction; empty?: string }) {
  const text = formatRelativeTime(value, t)
  if (!text) {
    return <span className="dev-mgmt-faint">{empty ?? EMPTY_VALUE}</span>
  }
  return (
    <Tooltip title={formatDateTime(value)}>
      <span>{text}</span>
    </Tooltip>
  )
}

export function DeviceTypeTag({ value }: { value?: string | null }) {
  if (!value) {
    return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
  }
  return <Tag color={DEVICE_TYPE_COLOR_MAP[value.trim().toLowerCase()] ?? 'default'}>{value}</Tag>
}

/** 设备状态胶囊：文案按状态码取本地化文案，未知状态码才回退后端描述（后端描述只有中文）。 */
export function DeviceStatusPill({
  status,
  description,
  t,
}: {
  status: number
  description?: string | null
  t: TFunction
}) {
  const tone = getDeviceStatusTone(status)
  const label = tone === 'unknown'
    ? description || String(status)
    : t(`posAdmin.devices.mgmt.statusTabs.${tone}`)
  return <span className={`dev-mgmt-pill dev-mgmt-pill-${tone}`}>{label}</span>
}

export function OnlineDot({ online, t }: { online: boolean; t: TFunction }) {
  return (
    <span className={`dev-mgmt-dot ${online ? 'dev-mgmt-dot-online' : 'dev-mgmt-dot-offline'}`}>
      {online ? t('posAdmin.devices.online') : t('posAdmin.devices.offline')}
    </span>
  )
}

export function formatStoreLabel(storeCode?: string | null, storeName?: string | null) {
  if (!storeCode) {
    return EMPTY_VALUE
  }
  return storeName ? `${storeCode} / ${storeName}` : storeCode
}

/** 分店单元格：名称在上、代码在下；没有名称时只显示代码。 */
export function StoreCell({ storeCode, storeName }: { storeCode?: string | null; storeName?: string | null }) {
  if (!storeCode) {
    return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
  }
  if (!storeName) {
    return <span className="dev-mgmt-mono">{storeCode}</span>
  }
  return (
    <div className="dev-mgmt-two">
      <span className="dev-mgmt-ellipsis" title={storeName}>{storeName}</span>
      <span className="dev-mgmt-sub dev-mgmt-mono">{storeCode}</span>
    </div>
  )
}
