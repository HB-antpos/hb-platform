import type { DeviceRegistrationItem } from '../../../types/deviceRegistration'
import type { PosApiEntryMode, PosApiEntryStoreItem } from '../../../types/posApiEntry'

import { compareDottedVersion } from './deviceManagementLogic'

/** 展示与确认弹窗里的固定顺序。 */
export const POS_API_ENTRY_MODES: readonly PosApiEntryMode[] = ['edge', 'aws', 'direct']

/** 三条入口对应的域名（客户端写死，后台只下发「选哪个」）。 */
export const POS_API_ENTRY_HOSTS: Record<PosApiEntryMode, string> = {
  edge: 'pos.hotbargain.vip',
  aws: 'pos-syd.hotbargain.vip',
  direct: 'hotbargain.vip',
}

/** WPF 从该版本起才认后台下发的首选入口；更低版本仍按旧默认走 EdgeOne。 */
export const MIN_ENTRY_SETTING_WPF_VERSION = '1.0.89'

/** 刚修改后多久内显示「等待生效」（设置经 receipt-profile/sync 约 1 分钟下发，夜间更慢）。 */
export const ENTRY_SETTING_PENDING_WINDOW_MS = 2 * 60_000

/** 从未设置过的分店按 EdgeOne 首选（与客户端「没收到过设置」的行为一致）。 */
export function getEffectiveMode(mode: PosApiEntryMode | null | undefined): PosApiEntryMode {
  return mode ?? 'edge'
}

/**
 * 备用顺序（三端一致，固定规则）：去掉首选后按 aws → edge → direct。
 * 首选 edge → [aws, direct]；首选 aws → [edge, direct]；首选 direct → [aws, edge]；null 按 edge。
 */
export function getFallbackOrder(mode: PosApiEntryMode | null | undefined): PosApiEntryMode[] {
  const preferred = getEffectiveMode(mode)
  return (['aws', 'edge', 'direct'] as const).filter((entry) => entry !== preferred)
}

/** 设备是否为 WPF 收银端（设备系统为 Windows）。 */
export function isWpfDevice(item: Pick<DeviceRegistrationItem, 'deviceSystem'>): boolean {
  return item.deviceSystem?.trim().toLowerCase() === 'windows'
}

/**
 * WPF 版本低于 1.0.89 → 不认后台设置。与后端 PosApiEntryRules.IsUnsupportedWpf 同口径：
 * 从未上报版本 / 版本无法解析的 Windows 老客户端同样算「版本过低」。
 * 手持 / iPad 没有可靠的 JS 版本号可比，不下结论。
 */
export function isEntrySettingUnsupported(
  item: Pick<DeviceRegistrationItem, 'deviceSystem' | 'appVersion'>,
): boolean {
  if (!isWpfDevice(item)) {
    return false
  }
  const delta = compareDottedVersion(item.appVersion, MIN_ENTRY_SETTING_WPF_VERSION)
  return delta === null || delta < 0
}

/**
 * 入口回显的新鲜窗口，与后端 PosApiEntryRules.EntryFreshWindow 一致：POS API 约每 10 分钟刷新一次
 * LastApiEntryAt，窗口取 15 分钟（刷新周期 + 余量），超过就当作「未知」，不展示过期的线路。
 */
export const ENTRY_FRESH_WINDOW_MS = 15 * 60_000

/** 入口记录是否新鲜；没有写入时间视为不新鲜。写入时间略超前于浏览器时钟（时钟漂移）按新鲜处理。 */
export function isEntryFresh(lastApiEntryAt: string | null | undefined, now: number): boolean {
  const timestamp = parseEntryTimestamp(lastApiEntryAt)
  return timestamp !== null && now - timestamp <= ENTRY_FRESH_WINDOW_MS
}

export type DeviceRouteState =
  /** 没有入口记录（从未请求 / 记录已过期） */
  | { kind: 'unknown' }
  | { kind: 'normal'; actual: PosApiEntryMode }
  /** 在线且实际入口 ≠ 所属分店首选 */
  | { kind: 'fallback'; actual: PosApiEntryMode; preferred: PosApiEntryMode }
  /** 版本过低：仍按旧默认走 EdgeOne，不参与回退判断 */
  | { kind: 'unsupported'; actual: PosApiEntryMode }

/**
 * 设备「线路」列状态。
 * - 版本过低优先：它本来就不认设置，实际入口与首选不同不是回退；没有新鲜入口记录时按旧默认 EdgeOne 展示。
 * - 入口记录过期（超过 15 分钟没刷新）按未知处理，不展示旧线路。
 * - 回退只对在线设备下结论：离线设备的最后入口不能说「正在回退」。
 * - 查不到所属分店首选（分店列表加载失败 / 设备无分店）时只显示实际入口，不做回退判断。
 */
export function getDeviceRouteState(
  item: Pick<DeviceRegistrationItem, 'deviceSystem' | 'appVersion' | 'lastApiEntry' | 'lastApiEntryAt'>,
  preferred: PosApiEntryMode | undefined,
  online: boolean,
  now: number = Date.now(),
): DeviceRouteState {
  const actual = isEntryFresh(item.lastApiEntryAt, now) ? item.lastApiEntry ?? null : null
  if (isEntrySettingUnsupported(item)) {
    return { kind: 'unsupported', actual: actual ?? 'edge' }
  }
  if (!actual) {
    return { kind: 'unknown' }
  }
  if (online && preferred && actual !== preferred) {
    return { kind: 'fallback', actual, preferred }
  }
  return { kind: 'normal', actual }
}

/** 设备列表「线路」筛选：三条线路按实际入口，「回退中」按回退判定。 */
export type DeviceRouteFilter = PosApiEntryMode | 'fallback'

export function matchesRouteFilter(state: DeviceRouteState, filter: DeviceRouteFilter | undefined): boolean {
  if (!filter) {
    return true
  }
  if (filter === 'fallback') {
    return state.kind === 'fallback'
  }
  return state.kind !== 'unknown' && state.actual === filter
}

/** 分店编号 → 生效首选，供设备列表对照；大小写与首尾空白不敏感。 */
export function buildPreferredModeMap(stores: readonly PosApiEntryStoreItem[]): Map<string, PosApiEntryMode> {
  const result = new Map<string, PosApiEntryMode>()
  for (const store of stores) {
    const code = store.storeCode.trim().toLowerCase()
    if (code) {
      result.set(code, getEffectiveMode(store.effectiveMode))
    }
  }
  return result
}

export function lookupPreferredMode(
  map: ReadonlyMap<string, PosApiEntryMode>,
  storeCode: string | null | undefined,
): PosApiEntryMode | undefined {
  const code = storeCode?.trim().toLowerCase()
  return code ? map.get(code) : undefined
}

export interface PosApiEntrySummary {
  storeCount: number
  /** 各线路作为首选的分店数（未设置按 EdgeOne 计入） */
  preferred: Record<PosApiEntryMode, number>
  /** 各线路实际承载的设备数 */
  actual: Record<PosApiEntryMode, number>
  /** 各线路承载的设备里，属于「回退过来的」台数 */
  fallbackOn: Record<PosApiEntryMode, number>
  unknownDevices: number
  fallbackTotal: number
  unsupportedTotal: number
  deviceTotal: number
}

export function summarizeStores(items: readonly PosApiEntryStoreItem[]): PosApiEntrySummary {
  const summary: PosApiEntrySummary = {
    storeCount: items.length,
    preferred: { edge: 0, aws: 0, direct: 0 },
    actual: { edge: 0, aws: 0, direct: 0 },
    fallbackOn: { edge: 0, aws: 0, direct: 0 },
    unknownDevices: 0,
    fallbackTotal: 0,
    unsupportedTotal: 0,
    deviceTotal: 0,
  }
  for (const item of items) {
    const preferred = getEffectiveMode(item.effectiveMode)
    summary.preferred[preferred] += 1
    summary.deviceTotal += item.devices.total
    summary.unknownDevices += item.devices.byEntry.unknown
    summary.fallbackTotal += item.devices.fallbackCount
    summary.unsupportedTotal += item.devices.unsupportedCount
    for (const entry of POS_API_ENTRY_MODES) {
      const devices = item.devices.byEntry[entry]
      summary.actual[entry] += devices
      if (entry !== preferred) {
        summary.fallbackOn[entry] += devices
      }
    }
  }
  return summary
}

export interface PosApiEntryStoreFilters {
  keyword: string
  /** 按生效首选筛选 */
  mode?: PosApiEntryMode
  onlyFallback: boolean
}

export function filterStores(
  items: readonly PosApiEntryStoreItem[],
  filters: PosApiEntryStoreFilters,
): PosApiEntryStoreItem[] {
  const needle = filters.keyword.trim().toLowerCase()
  return items.filter((item) => {
    if (filters.mode && getEffectiveMode(item.effectiveMode) !== filters.mode) {
      return false
    }
    if (filters.onlyFallback && item.devices.fallbackCount <= 0) {
      return false
    }
    return !needle || `${item.storeCode} ${item.storeName}`.toLowerCase().includes(needle)
  })
}

export type EntrySegmentKind = 'preferred' | 'fallback' | 'unsupported' | 'unknown'

export interface EntrySegment {
  kind: EntrySegmentKind
  /** preferred 为首选线路；fallback 为回退到的线路；其余为空 */
  entry?: PosApiEntryMode
  count: number
}

/**
 * 「设备实际线路」分布条的分段：首选 → 回退到的各线路（按备用顺序）→ 版本过低 → 未上报。
 * 空分段不返回；全部为空时返回空数组（页面显示「--」）。
 */
export function getEntrySegments(item: PosApiEntryStoreItem): EntrySegment[] {
  const preferred = getEffectiveMode(item.effectiveMode)
  const { byEntry, unsupportedCount } = item.devices
  const segments: EntrySegment[] = [
    { kind: 'preferred', entry: preferred, count: byEntry[preferred] },
    ...getFallbackOrder(preferred).map((entry) => ({ kind: 'fallback' as const, entry, count: byEntry[entry] })),
    { kind: 'unsupported', count: unsupportedCount },
    { kind: 'unknown', count: byEntry.unknown },
  ]
  return segments.filter((segment) => segment.count > 0)
}

export type StoreAttentionKind = 'fallback' | 'unsupported' | 'offline' | 'pending'

export interface StoreAttention {
  kind: StoreAttentionKind
  count?: number
}

/** 解析后端的 UTC 时间：没带时区后缀的按 UTC 处理（后端 datetime2 存 UTC）。无效返回 null。 */
export function parseEntryTimestamp(value: string | null | undefined): number | null {
  if (!value) {
    return null
  }
  const text = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`
  const timestamp = Date.parse(text)
  return Number.isNaN(timestamp) ? null : timestamp
}

/** 「需要关注」列：回退中 / 版本过低 / 离线 / 刚修改等待生效，可同时出现多项。 */
export function getStoreAttention(item: PosApiEntryStoreItem, now: number): StoreAttention[] {
  const result: StoreAttention[] = []
  if (item.devices.fallbackCount > 0) {
    result.push({ kind: 'fallback', count: item.devices.fallbackCount })
  }
  if (item.devices.unsupportedCount > 0) {
    result.push({ kind: 'unsupported', count: item.devices.unsupportedCount })
  }
  const offline = item.devices.total - item.devices.online
  if (offline > 0) {
    result.push({ kind: 'offline', count: offline })
  }
  const updatedAt = parseEntryTimestamp(item.updatedAt)
  if (updatedAt !== null && now - updatedAt >= 0 && now - updatedAt < ENTRY_SETTING_PENDING_WINDOW_MS) {
    result.push({ kind: 'pending' })
  }
  return result
}

/** 确认弹窗里「N 台均已支持 / 其中 M 台版本过低」所需的合计。 */
export function summarizeTargets(items: readonly PosApiEntryStoreItem[]) {
  const total = items.reduce((sum, item) => sum + item.devices.total, 0)
  const unsupported = items.reduce((sum, item) => sum + item.devices.unsupportedCount, 0)
  return { total, unsupported, supported: Math.max(total - unsupported, 0) }
}

/** 弹窗副标题里的分店名：最多列前 3 家，其余折叠成「等 N 家」。 */
export function previewStoreNames(items: readonly PosApiEntryStoreItem[], limit = 3) {
  return {
    names: items.slice(0, limit).map((item) => item.storeName || item.storeCode),
    rest: Math.max(items.length - limit, 0),
  }
}
