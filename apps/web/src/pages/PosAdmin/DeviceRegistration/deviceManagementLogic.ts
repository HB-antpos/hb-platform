import type { DeviceRegistrationItem } from '../../../types/deviceRegistration'

/** 设备状态码，与后端 POSM_设备注册信息表.设备状态 一致。 */
export const DEVICE_STATUS = {
  pending: -1,
  disabled: 0,
  enabled: 1,
  locked: 2,
  unregistered: 3,
} as const

export type DeviceStatusTab = 'all' | 'pending' | 'enabled' | 'disabled' | 'locked'

export const DEVICE_STATUS_TABS: DeviceStatusTab[] = ['all', 'pending', 'enabled', 'disabled', 'locked']

const STATUS_TAB_CODES: Record<Exclude<DeviceStatusTab, 'all'>, number> = {
  pending: DEVICE_STATUS.pending,
  enabled: DEVICE_STATUS.enabled,
  disabled: DEVICE_STATUS.disabled,
  locked: DEVICE_STATUS.locked,
}

export type DeviceOnlineFilter = 'online' | 'offline'

export interface DeviceListFilters {
  status: DeviceStatusTab
  keyword: string
  deviceType?: string
  deviceSystem?: string
  online?: DeviceOnlineFilter
}

export type DeviceStatusTone = 'pending' | 'enabled' | 'disabled' | 'locked' | 'unknown'

export function getDeviceStatusTone(status: number): DeviceStatusTone {
  switch (status) {
    case DEVICE_STATUS.pending:
      return 'pending'
    case DEVICE_STATUS.enabled:
      return 'enabled'
    case DEVICE_STATUS.disabled:
      return 'disabled'
    case DEVICE_STATUS.locked:
      return 'locked'
    default:
      return 'unknown'
  }
}

function normalizeText(value?: string | null) {
  return value?.trim().toLowerCase() ?? ''
}

/**
 * 关键字匹配设备编号、硬件标识、分店代码/名称、备注和当前收银员。
 * 后端 keyword 只覆盖前四项中的三项，这里在已加载的全量列表上做，口径更宽。
 */
export function matchesDeviceKeyword(
  item: DeviceRegistrationItem,
  keyword: string,
  storeName?: string,
): boolean {
  const needle = normalizeText(keyword)
  if (!needle) {
    return true
  }

  return [
    item.systemDeviceNumber,
    item.hardwareId,
    item.storeCode,
    storeName ?? item.storeName,
    item.remark,
    item.currentCashierName,
  ].some((value) => normalizeText(value).includes(needle))
}

/**
 * 除状态页签外的筛选：页签计数要基于这一步的结果，切页签时各页签数字才对得上。
 */
export function filterDevicesExceptStatus(
  items: DeviceRegistrationItem[],
  filters: Omit<DeviceListFilters, 'status'>,
  isOnline: (item: DeviceRegistrationItem) => boolean,
  getStoreName: (storeCode?: string | null) => string | undefined = () => undefined,
): DeviceRegistrationItem[] {
  const deviceType = normalizeText(filters.deviceType)
  const deviceSystem = normalizeText(filters.deviceSystem)

  return items.filter((item) => {
    if (deviceType && normalizeText(item.deviceType) !== deviceType) {
      return false
    }
    if (deviceSystem && normalizeText(item.deviceSystem) !== deviceSystem) {
      return false
    }
    if (filters.online && (filters.online === 'online') !== isOnline(item)) {
      return false
    }
    return matchesDeviceKeyword(item, filters.keyword, getStoreName(item.storeCode))
  })
}

export function filterDevicesByStatus(
  items: DeviceRegistrationItem[],
  status: DeviceStatusTab,
): DeviceRegistrationItem[] {
  if (status === 'all') {
    return items
  }
  const code = STATUS_TAB_CODES[status]
  return items.filter((item) => item.status === code)
}

export function countDevicesByStatus(items: DeviceRegistrationItem[]): Record<DeviceStatusTab, number> {
  const counts: Record<DeviceStatusTab, number> = {
    all: items.length,
    pending: 0,
    enabled: 0,
    disabled: 0,
    locked: 0,
  }
  for (const item of items) {
    const tone = getDeviceStatusTone(item.status)
    if (tone !== 'unknown') {
      counts[tone] += 1
    }
  }
  return counts
}

/**
 * 下拉选项取已加载设备里实际出现的值（生产里有 StorePDA、WarehousePDA、Android 11 等历史值），
 * 再并入固定候选并保留当前选中值，避免选中值因换分店而从选项里消失。
 */
export function collectDistinctOptions(
  values: (string | null | undefined)[],
  extras: readonly string[] = [],
): string[] {
  const seen = new Map<string, string>()
  for (const raw of [...extras, ...values]) {
    const value = raw?.trim()
    if (value && !seen.has(value.toLowerCase())) {
      seen.set(value.toLowerCase(), value)
    }
  }
  return [...seen.values()].sort((left, right) => left.localeCompare(right))
}

export type DeviceStatusAction = 'activate' | 'disable' | 'lock'

/**
 * 每种状态只给一个主操作：待确认/禁用/锁定 → 启用，已启用 → 禁用；
 * 其余仍可执行的动作收进「更多」菜单，避免旧版每行同时摆三个状态按钮。
 */
export function getDeviceStatusActions(status: number): {
  primary: DeviceStatusAction
  secondary: DeviceStatusAction[]
} {
  if (status === DEVICE_STATUS.enabled) {
    return { primary: 'disable', secondary: ['lock'] }
  }
  if (status === DEVICE_STATUS.locked) {
    return { primary: 'activate', secondary: ['disable'] }
  }
  if (status === DEVICE_STATUS.disabled) {
    return { primary: 'activate', secondary: ['lock'] }
  }
  return { primary: 'activate', secondary: ['disable', 'lock'] }
}

export type RelativeTimeParts =
  | { unit: 'justNow' }
  | { unit: 'minutes' | 'hours' | 'days'; count: number }
  | { unit: 'date'; date: Date }

/** 心跳、上报时间用相对时间展示（完整时间放 Tooltip）；超过 30 天回退成日期。 */
export function getRelativeTimeParts(value: string | null | undefined, now = Date.now()): RelativeTimeParts | null {
  if (!value) {
    return null
  }
  const timestamp = Date.parse(value)
  if (Number.isNaN(timestamp)) {
    return null
  }

  const diffMs = Math.max(0, now - timestamp)
  const minutes = Math.floor(diffMs / 60_000)
  if (minutes < 1) {
    return { unit: 'justNow' }
  }
  if (minutes < 60) {
    return { unit: 'minutes', count: minutes }
  }
  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    return { unit: 'hours', count: hours }
  }
  const days = Math.floor(hours / 24)
  if (days <= 30) {
    return { unit: 'days', count: days }
  }
  return { unit: 'date', date: new Date(timestamp) }
}

export function formatDateOnly(value?: string | null): string | null {
  if (!value) {
    return null
  }
  const timestamp = Date.parse(value)
  if (Number.isNaN(timestamp)) {
    return value
  }
  const date = new Date(timestamp)
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

export function compareDateDesc(left?: string | null, right?: string | null): number {
  const leftTime = left ? Date.parse(left) : Number.NaN
  const rightTime = right ? Date.parse(right) : Number.NaN
  return (Number.isNaN(rightTime) ? 0 : rightTime) - (Number.isNaN(leftTime) ? 0 : leftTime)
}

export interface CountedOption {
  value: string
  count: number
}

/**
 * 筛选下拉只列当前范围里真实存在的取值并附带台数（按台数降序），
 * 不混入编辑表单的固定候选——否则会出现选了也必然为空的选项（如生产没有 Admin 类型设备）。
 * 取值按大小写不敏感归并，与后端列排序规则 Chinese_PRC_90_CI_AS 的等值匹配一致；
 * 当前选中值即使在新范围里不存在也保留（计数 0），避免换分店后下拉显示裸值。
 */
export function buildCountedOptions(
  values: (string | null | undefined)[],
  selected?: string,
): CountedOption[] {
  const groups = new Map<string, CountedOption>()
  for (const raw of values) {
    const value = raw?.trim()
    if (!value) {
      continue
    }
    const key = value.toLowerCase()
    const group = groups.get(key)
    if (group) {
      group.count += 1
    } else {
      groups.set(key, { value, count: 1 })
    }
  }

  const selectedValue = selected?.trim()
  if (selectedValue && !groups.has(selectedValue.toLowerCase())) {
    groups.set(selectedValue.toLowerCase(), { value: selectedValue, count: 0 })
  }

  return [...groups.values()].sort(
    (left, right) => right.count - left.count || left.value.localeCompare(right.value),
  )
}
