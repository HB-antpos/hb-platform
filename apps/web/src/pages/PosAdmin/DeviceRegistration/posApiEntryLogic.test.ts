import type { DeviceRegistrationItem } from '../../../types/deviceRegistration'
import type { PosApiEntryStoreItem } from '../../../types/posApiEntry'

import {
  buildPreferredModeMap,
  filterStores,
  getDeviceRouteState,
  getEffectiveMode,
  getEntrySegments,
  getFallbackOrder,
  getStoreAttention,
  isEntrySettingUnsupported,
  lookupPreferredMode,
  matchesRouteFilter,
  parseEntryTimestamp,
  previewStoreNames,
  summarizeStores,
  summarizeTargets,
} from './posApiEntryLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const left = JSON.stringify(actual)
  const right = JSON.stringify(expected)
  if (left !== right) {
    throw new Error(`${message}: expected ${right}, got ${left}`)
  }
}

function store(
  overrides: Partial<Omit<PosApiEntryStoreItem, 'devices'>> & { devices?: Partial<PosApiEntryStoreItem['devices']> },
): PosApiEntryStoreItem {
  const { devices, ...rest } = overrides
  return {
    storeGuid: 'g-1',
    storeCode: '1042',
    storeName: '测试店',
    mode: null,
    effectiveMode: 'edge',
    updatedAt: null,
    updatedBy: null,
    ...rest,
    devices: {
      total: 0,
      online: 0,
      byEntry: { edge: 0, aws: 0, direct: 0, unknown: 0 },
      fallbackCount: 0,
      unsupportedCount: 0,
      ...devices,
    },
  }
}

const NOW = Date.parse('2026-10-24T01:00:00Z')
const FRESH_AT = '2026-10-24T00:55:00Z'

function device(
  overrides: Partial<DeviceRegistrationItem>,
): Pick<DeviceRegistrationItem, 'deviceSystem' | 'appVersion' | 'lastApiEntry' | 'lastApiEntryAt'> {
  return { deviceSystem: 'Windows', appVersion: '1.0.89', lastApiEntry: 'edge', lastApiEntryAt: FRESH_AT, ...overrides }
}

// 备用顺序：去掉首选后按 aws → edge → direct；null 按 edge
assertDeepEqual(getFallbackOrder('edge'), ['aws', 'direct'], '首选 edge')
assertDeepEqual(getFallbackOrder('aws'), ['edge', 'direct'], '首选 aws')
assertDeepEqual(getFallbackOrder('direct'), ['aws', 'edge'], '首选 direct')
assertDeepEqual(getFallbackOrder(null), ['aws', 'direct'], 'null 按 edge')
assertDeepEqual(getFallbackOrder(undefined), ['aws', 'direct'], 'undefined 按 edge')
assertEqual(getEffectiveMode(null), 'edge', '未设置按 edge')
assertEqual(getEffectiveMode('direct'), 'direct', '显式值原样返回')

// 版本是否支持：只对 WPF 判断；1.0.89 起支持；版本缺失 / 无法解析不下结论
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: '1.0.87' }), true, '1.0.87 不支持')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: '1.0.88' }), true, '1.0.88 不支持')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: '1.0.89' }), false, '1.0.89 支持')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: '1.0.100' }), false, '1.0.100 按数字比较，支持')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'windows', appVersion: '1.0.2' }), true, '系统名不区分大小写')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: null }), true, '从未上报版本的 Windows 老客户端算版本过低（与后端一致）')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Windows', appVersion: 'abc' }), true, '版本无法解析同样算版本过低（与后端一致）')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'Android', appVersion: '0.1.2' }), false, '手持不按 WPF 版本判断')
assertEqual(isEntrySettingUnsupported({ deviceSystem: 'iPadOS', appVersion: '0.2.0' }), false, 'iPad 不按 WPF 版本判断')

// 设备线路状态
assertDeepEqual(getDeviceRouteState(device({ lastApiEntry: 'edge' }), 'edge', true, NOW), { kind: 'normal', actual: 'edge' }, '实际 = 首选')
assertDeepEqual(
  getDeviceRouteState(device({ lastApiEntry: 'aws' }), 'edge', true, NOW),
  { kind: 'fallback', actual: 'aws', preferred: 'edge' },
  '在线且实际 ≠ 首选 → 回退中',
)
assertDeepEqual(getDeviceRouteState(device({ lastApiEntry: 'aws' }), 'edge', false, NOW), { kind: 'normal', actual: 'aws' }, '离线设备不判回退')
assertDeepEqual(getDeviceRouteState(device({ lastApiEntry: 'aws' }), undefined, true, NOW), { kind: 'normal', actual: 'aws' }, '查不到首选不判回退')
assertDeepEqual(getDeviceRouteState(device({ lastApiEntry: null }), 'edge', true, NOW), { kind: 'unknown' }, '没有入口记录')
assertDeepEqual(
  getDeviceRouteState(device({ lastApiEntry: 'aws', lastApiEntryAt: '2026-10-24T00:40:00Z' }), 'edge', true, NOW),
  { kind: 'unknown' },
  '入口记录超过 15 分钟没刷新按未知，不判回退',
)
assertDeepEqual(
  getDeviceRouteState(device({ lastApiEntry: 'aws', lastApiEntryAt: null }), 'edge', true, NOW),
  { kind: 'unknown' },
  '没有写入时间按未知',
)
assertDeepEqual(
  getDeviceRouteState(device({ lastApiEntry: 'aws', lastApiEntryAt: '2026-10-24T01:02:00Z' }), 'edge', true, NOW),
  { kind: 'fallback', actual: 'aws', preferred: 'edge' },
  '写入时间略超前于浏览器时钟按新鲜处理',
)
assertDeepEqual(
  getDeviceRouteState(device({ appVersion: '1.0.87', lastApiEntry: 'edge' }), 'aws', true, NOW),
  { kind: 'unsupported', actual: 'edge' },
  '版本过低优先于回退判断',
)
assertDeepEqual(
  getDeviceRouteState(device({ appVersion: '1.0.87', lastApiEntry: null }), 'aws', true, NOW),
  { kind: 'unsupported', actual: 'edge' },
  '版本过低且无记录按旧默认 EdgeOne 展示',
)

// 线路筛选
const fallbackState = getDeviceRouteState(device({ lastApiEntry: 'aws' }), 'edge', true, NOW)
const awsNormal = getDeviceRouteState(device({ lastApiEntry: 'aws' }), 'aws', true, NOW)
const unknownState = getDeviceRouteState(device({ lastApiEntry: null }), 'edge', true, NOW)
assertEqual(matchesRouteFilter(fallbackState, undefined), true, '不筛选全部通过')
assertEqual(matchesRouteFilter(fallbackState, 'fallback'), true, '回退中筛选命中')
assertEqual(matchesRouteFilter(awsNormal, 'fallback'), false, '正常设备不算回退中')
assertEqual(matchesRouteFilter(fallbackState, 'aws'), true, '回退到 aws 的设备也算实际走 aws')
assertEqual(matchesRouteFilter(awsNormal, 'edge'), false, '实际 aws 不匹配 edge')
assertEqual(matchesRouteFilter(unknownState, 'edge'), false, '没有记录的设备不匹配任何具体线路')

// 分店编号 → 首选
const preferredMap = buildPreferredModeMap([
  store({ storeCode: ' ab12 ', effectiveMode: 'aws' }),
  store({ storeGuid: 'g-2', storeCode: '', effectiveMode: 'direct' }),
])
assertEqual(lookupPreferredMode(preferredMap, 'AB12'), 'aws', '编号不区分大小写与空白')
assertEqual(lookupPreferredMode(preferredMap, 'zzz'), undefined, '未知分店')
assertEqual(lookupPreferredMode(preferredMap, null), undefined, '设备无分店')

// 统计汇总
const stores = [
  store({
    storeGuid: 'a', mode: 'aws', effectiveMode: 'aws',
    devices: { total: 4, online: 4, byEntry: { edge: 0, aws: 3, direct: 0, unknown: 0 }, unsupportedCount: 1 },
  }),
  store({
    storeGuid: 'b', effectiveMode: 'edge',
    devices: { total: 4, online: 3, byEntry: { edge: 2, aws: 2, direct: 0, unknown: 0 }, fallbackCount: 2 },
  }),
  store({
    storeGuid: 'c', mode: 'direct', effectiveMode: 'direct',
    devices: { total: 3, online: 3, byEntry: { edge: 0, aws: 0, direct: 3, unknown: 0 } },
  }),
]
const summary = summarizeStores(stores)
assertDeepEqual(summary.preferred, { edge: 1, aws: 1, direct: 1 }, '首选分布')
assertDeepEqual(summary.actual, { edge: 2, aws: 5, direct: 3 }, '实际台数')
assertDeepEqual(summary.fallbackOn, { edge: 0, aws: 2, direct: 0 }, '各线路上回退过来的台数')
assertEqual(summary.fallbackTotal, 2, '回退合计')
assertEqual(summary.unsupportedTotal, 1, '版本过低合计')
assertEqual(summary.deviceTotal, 11, '设备合计')
assertEqual(summarizeStores([]).storeCount, 0, '空列表不报错')

// 分店筛选
assertEqual(filterStores(stores, { keyword: '', onlyFallback: false }).length, 3, '无筛选')
assertEqual(filterStores(stores, { keyword: '', mode: 'aws', onlyFallback: false }).length, 1, '按首选筛选')
assertEqual(filterStores(stores, { keyword: '', mode: 'edge', onlyFallback: false })[0]?.storeGuid, 'b', '未设置的分店按 edge 筛选')
assertEqual(filterStores(stores, { keyword: '', onlyFallback: true }).length, 1, '只看有回退的分店')
assertEqual(filterStores(stores, { keyword: ' 测试 ', onlyFallback: false }).length, 3, '关键字匹配名称并忽略首尾空白')
assertEqual(filterStores(stores, { keyword: '9999', onlyFallback: false }).length, 0, '关键字不匹配')

// 实际线路分布条分段
assertDeepEqual(
  getEntrySegments(stores[1]),
  [{ kind: 'preferred', entry: 'edge', count: 2 }, { kind: 'fallback', entry: 'aws', count: 2 }],
  '回退分段',
)
assertDeepEqual(
  getEntrySegments(stores[0]),
  [{ kind: 'preferred', entry: 'aws', count: 3 }, { kind: 'unsupported', count: 1 }],
  '版本过低分段',
)
assertDeepEqual(getEntrySegments(store({})), [], '没有设备时无分段')
assertDeepEqual(
  getEntrySegments(store({ devices: { total: 1, byEntry: { edge: 0, aws: 0, direct: 0, unknown: 1 } } })),
  [{ kind: 'unknown', count: 1 }],
  '未上报分段',
)

// 需要关注
assertDeepEqual(getStoreAttention(store({}), NOW), [], '无事项')
assertDeepEqual(
  getStoreAttention(stores[1], NOW),
  [{ kind: 'fallback', count: 2 }, { kind: 'offline', count: 1 }],
  '回退 + 离线',
)
assertDeepEqual(getStoreAttention(stores[0], NOW), [{ kind: 'unsupported', count: 1 }], '版本过低')
assertDeepEqual(
  getStoreAttention(store({ updatedAt: '2026-10-24T00:59:00' }), NOW),
  [{ kind: 'pending' }],
  '1 分钟前修改（无时区后缀按 UTC）→ 等待生效',
)
assertDeepEqual(getStoreAttention(store({ updatedAt: '2026-10-24T00:50:00Z' }), NOW), [], '10 分钟前修改不再等待生效')
assertDeepEqual(getStoreAttention(store({ updatedAt: '2026-10-24T01:30:00Z' }), NOW), [], '时钟偏差导致的未来时间不显示等待生效')

assertEqual(parseEntryTimestamp('2026-10-24T01:00:00'), NOW, '无时区后缀按 UTC')
assertEqual(parseEntryTimestamp('2026-10-24T11:00:00+10:00'), NOW, '带偏移的时间按偏移解析')
assertEqual(parseEntryTimestamp('not-a-date'), null, '无效时间')
assertEqual(parseEntryTimestamp(null), null, '空时间')

// 确认弹窗合计
const targets = summarizeTargets([stores[0], stores[1]])
assertEqual(targets.total, 8, '弹窗设备合计')
assertEqual(targets.unsupported, 1, '弹窗版本过低合计')
assertEqual(targets.supported, 7, '弹窗支持合计')
const preview = previewStoreNames([
  store({ storeName: 'A' }), store({ storeName: 'B' }), store({ storeName: '', storeCode: 'C3' }), store({ storeName: 'D' }),
])
assertDeepEqual(preview, { names: ['A', 'B', 'C3'], rest: 1 }, '最多列 3 家，其余折叠；无名称用编号')

console.log('posApiEntryLogic tests passed')
