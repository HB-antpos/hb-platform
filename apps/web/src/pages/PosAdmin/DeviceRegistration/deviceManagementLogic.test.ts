import type { DeviceRegistrationItem } from '../../../types/deviceRegistration'

import {
  buildCountedOptions,
  collectDistinctOptions,
  countDevicesByStatus,
  filterDevicesByStatus,
  filterDevicesExceptStatus,
  getDeviceStatusActions,
  getRelativeTimeParts,
  formatDateOnly,
} from './deviceManagementLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function device(overrides: Partial<DeviceRegistrationItem>): DeviceRegistrationItem {
  return {
    id: 1,
    hardwareId: 'HW-1',
    systemDeviceNumber: 'POS_1023_0001',
    storeCode: '1023',
    storeName: null,
    deviceType: 'POS',
    deviceSystem: 'Windows',
    status: 1,
    statusDescription: '启用',
    allowTransactions: true,
    isOnline: false,
    ...overrides,
  }
}

const items = [
  device({ id: 1, status: -1, deviceType: 'POS', deviceSystem: 'Windows', remark: '前台 2 号机' }),
  device({ id: 2, status: 1, deviceType: 'StorePDA', deviceSystem: 'Android', isOnline: true }),
  device({ id: 3, status: 0, deviceType: 'pos', deviceSystem: 'iPadOS', currentCashierName: 'Lily Chen' }),
  device({ id: 4, status: 2, deviceType: 'Mobile', deviceSystem: 'Android', storeCode: '1059' }),
  device({ id: 5, status: 3, deviceType: 'POS', deviceSystem: 'Windows' }),
]

const isOnline = (item: DeviceRegistrationItem) => item.isOnline

// 计数：未注册(3) 只计入「全部」，不落到任何状态页签。
const counts = countDevicesByStatus(items)
assertEqual(counts.all, 5, '全部计数')
assertEqual(counts.pending, 1, '待确认计数')
assertEqual(counts.enabled, 1, '启用计数')
assertEqual(counts.disabled, 1, '禁用计数')
assertEqual(counts.locked, 1, '锁定计数')

assertEqual(filterDevicesByStatus(items, 'pending')[0]?.id, 1, '待确认页签只留 -1')
assertEqual(filterDevicesByStatus(items, 'all').length, 5, '全部页签不过滤')

// 类型、系统按大小写不敏感的精确匹配（与后端 设备类型 == deviceType 同口径，但容忍历史大小写）。
assertEqual(
  filterDevicesExceptStatus(items, { keyword: '', deviceType: 'POS' }, isOnline).length,
  3,
  '类型筛选 POS 应包含小写 pos',
)
assertEqual(
  filterDevicesExceptStatus(items, { keyword: '', deviceSystem: 'android' }, isOnline).length,
  2,
  '系统筛选',
)
assertEqual(
  filterDevicesExceptStatus(items, { keyword: '', online: 'online' }, isOnline)[0]?.id,
  2,
  '只看在线',
)
assertEqual(
  filterDevicesExceptStatus(items, { keyword: '', online: 'offline' }, isOnline).length,
  4,
  '只看离线',
)

// 关键字覆盖备注、收银员、分店名称。
assertEqual(filterDevicesExceptStatus(items, { keyword: '2 号机' }, isOnline)[0]?.id, 1, '关键字匹配备注')
assertEqual(filterDevicesExceptStatus(items, { keyword: 'lily' }, isOnline)[0]?.id, 3, '关键字匹配收银员')
assertEqual(
  filterDevicesExceptStatus(
    items,
    { keyword: 'bankstown' },
    isOnline,
    (storeCode) => (storeCode === '1059' ? 'Bankstown' : undefined),
  )[0]?.id,
  4,
  '关键字匹配分店名称',
)

// 选项：去重（大小写不敏感）、并入固定候选、排序。
const typeOptions = collectDistinctOptions(items.map((item) => item.deviceType), ['Admin', 'POS'])
assertEqual(typeOptions.join(','), 'Admin,Mobile,POS,StorePDA', '类型选项')

// 每种状态的主操作。
assertEqual(getDeviceStatusActions(-1).primary, 'activate', '待确认主操作为启用')
assertEqual(getDeviceStatusActions(1).primary, 'disable', '已启用主操作为禁用')
assert(getDeviceStatusActions(1).secondary.includes('lock'), '已启用可锁定')
assertEqual(getDeviceStatusActions(2).primary, 'activate', '锁定主操作为启用')
assert(!getDeviceStatusActions(2).secondary.includes('lock'), '已锁定不再提供锁定')
assert(!getDeviceStatusActions(0).secondary.includes('disable'), '已禁用不再提供禁用')

// 相对时间。
const now = Date.parse('2026-10-06T10:00:00Z')
assertEqual(getRelativeTimeParts('2026-10-06T09:59:40Z', now)?.unit, 'justNow', '一分钟内为刚刚')
const minutes = getRelativeTimeParts('2026-10-06T09:45:00Z', now)
assert(minutes?.unit === 'minutes' && minutes.count === 15, '15 分钟前')
const hours = getRelativeTimeParts('2026-10-06T07:00:00Z', now)
assert(hours?.unit === 'hours' && hours.count === 3, '3 小时前')
const days = getRelativeTimeParts('2026-10-01T10:00:00Z', now)
assert(days?.unit === 'days' && days.count === 5, '5 天前')
assertEqual(getRelativeTimeParts('2026-07-01T10:00:00Z', now)?.unit, 'date', '超过 30 天回退为日期')
assertEqual(getRelativeTimeParts(null, now), null, '空值')
assertEqual(getRelativeTimeParts('not-a-date', now), null, '非法时间')

assertEqual(formatDateOnly('2026-01-05T12:00:00'), '2026-01-05', '日期格式')
assertEqual(formatDateOnly(undefined), null, '空日期')

// 筛选选项：只列真实存在的类型并计数，不混入固定候选（生产没有 Admin 设备）。
const countedTypes = buildCountedOptions(items.map((item) => item.deviceType))
assertEqual(
  countedTypes.map((option) => `${option.value}:${option.count}`).join(','),
  'POS:3,Mobile:1,StorePDA:1',
  '类型筛选选项按台数降序且大小写归并',
)
assert(!countedTypes.some((option) => option.value === 'Admin'), '筛选选项不应出现不存在的 Admin')
const withSelected = buildCountedOptions(['POS'], 'WarehousePDA')
assertEqual(withSelected.find((option) => option.value === 'WarehousePDA')?.count, 0, '保留当前选中值且计数为 0')
assertEqual(buildCountedOptions(['POS', ' ', null, undefined]).length, 1, '空值不进选项')

console.log('deviceManagementLogic.test: ok')
