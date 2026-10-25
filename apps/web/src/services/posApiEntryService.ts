import type { ApiResponse } from '../types/api'
import type {
  PosApiEntryDeviceStats,
  PosApiEntryMode,
  PosApiEntryStoreItem,
  PosApiEntryUpdateResult,
  PosApiEntryUsageBucket,
  PosApiEntryUsageCounts,
  PosApiEntryUsageEntry,
  PosApiEntryUsageEntryItem,
  PosApiEntryUsageGranularity,
  PosApiEntryUsageQuery,
  PosApiEntryUsageResult,
  PosApiEntryUsageStore,
} from '../types/posApiEntry'
import request, { unwrapApiData } from '../utils/request'

// 路由与后端 PosApiEntryController 一致。查看权限 DeviceRegistration.View，修改权限 DeviceRegistration.Manage。
const POS_API_ENTRY_BASE = '/api/react/v1/pos-api-entry'

/** 单次批量修改的分店数上限（与分店小票下发同口径，避免一次提交过大）。 */
export const POS_API_ENTRY_MAX_STORES_PER_UPDATE = 100

/** 未知 / 空值一律返回 null：旧后端或新增线路时前端不崩，也不会把未知值当成某条线路展示。 */
export function normalizePosApiEntryMode(value: unknown): PosApiEntryMode | null {
  if (typeof value !== 'string') {
    return null
  }
  const normalized = value.trim().toLowerCase()
  return normalized === 'edge' || normalized === 'aws' || normalized === 'direct' ? normalized : null
}

function count(value: unknown) {
  const numeric = typeof value === 'string' && value.trim() ? Number(value) : value
  return typeof numeric === 'number' && Number.isFinite(numeric) && numeric > 0 ? Math.floor(numeric) : 0
}

function text(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function nullableText(value: unknown) {
  return typeof value === 'string' && value ? value : null
}

function normalizeDeviceStats(raw: unknown): PosApiEntryDeviceStats {
  const record = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  const byEntry = (record.byEntry && typeof record.byEntry === 'object' ? record.byEntry : {}) as Record<string, unknown>
  return {
    total: count(record.total),
    online: count(record.online),
    byEntry: {
      edge: count(byEntry.edge),
      aws: count(byEntry.aws),
      direct: count(byEntry.direct),
      unknown: count(byEntry.unknown),
    },
    fallbackCount: count(record.fallbackCount),
    unsupportedCount: count(record.unsupportedCount),
  }
}

export function normalizePosApiEntryStores(payload: unknown): PosApiEntryStoreItem[] {
  if (!Array.isArray(payload)) {
    return []
  }
  const result: PosApiEntryStoreItem[] = []
  for (const raw of payload) {
    if (!raw || typeof raw !== 'object') {
      continue
    }
    const record = raw as Record<string, unknown>
    const storeGuid = text(record.storeGuid)
    if (!storeGuid) {
      continue
    }
    const mode = normalizePosApiEntryMode(record.mode)
    result.push({
      storeGuid,
      storeCode: text(record.storeCode),
      storeName: text(record.storeName),
      mode,
      // 后端没给 effectiveMode 时按「首选 ?? edge」自行推导，与客户端规则一致
      effectiveMode: normalizePosApiEntryMode(record.effectiveMode) ?? mode ?? 'edge',
      updatedAt: nullableText(record.updatedAt),
      updatedBy: nullableText(record.updatedBy),
      devices: normalizeDeviceStats(record.devices),
    })
  }
  return result
}

/** 每家分店的收银入口设置 + 设备实际入口统计。 */
export async function getPosApiEntryStores(): Promise<PosApiEntryStoreItem[]> {
  const response = await request.get<ApiResponse<unknown>>(`${POS_API_ENTRY_BASE}/stores`)
  return normalizePosApiEntryStores(unwrapApiData(response))
}

/** 批量设置首选入口；返回实际更新与跳过（本来就是该值）的家数。 */
export async function updatePosApiEntryStores(
  storeGuids: string[],
  mode: PosApiEntryMode,
): Promise<PosApiEntryUpdateResult> {
  const response = await request.put<ApiResponse<PosApiEntryUpdateResult>>(`${POS_API_ENTRY_BASE}/stores`, {
    storeGuids,
    mode,
  })
  const data = unwrapApiData(response) as Partial<PosApiEntryUpdateResult> | undefined
  return { updated: count(data?.updated), skipped: count(data?.skipped) }
}

// ───────────────────────── 线路用量统计 ─────────────────────────

/** 用量统计里线路的固定输出顺序：三条线路在前，识别不出的 unknown 最后。 */
export const POS_API_ENTRY_USAGE_ENTRIES: readonly PosApiEntryUsageEntry[] = ['edge', 'aws', 'direct', 'unknown']

/** 与后端 PosApiEntryUsageService.MaxRangeDays 一致：查询范围（含首尾）最多 400 天。 */
export const POS_API_ENTRY_USAGE_MAX_RANGE_DAYS = 400

export function normalizePosApiEntryUsageEntry(value: unknown): PosApiEntryUsageEntry {
  // 未知值并入 unknown，保证前端展示的请求总数与后端一致
  return normalizePosApiEntryMode(value) ?? 'unknown'
}

function normalizeGranularity(value: unknown, fallback: PosApiEntryUsageGranularity): PosApiEntryUsageGranularity {
  return value === 'day' || value === 'week' || value === 'month' ? value : fallback
}

function normalizeCounts(raw: unknown): PosApiEntryUsageCounts {
  const record = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  return { requests: count(record.requests), status4xx: count(record.status4xx), status5xx: count(record.status5xx) }
}

/**
 * 把后端返回的 entries 规整成固定四项（缺的补 0、重复的 / 未知的并入对应项），
 * 这样图表和表格永远拿到「edge, aws, direct, unknown」四个格子，不用再判空。
 */
function normalizeUsageEntries(raw: unknown): PosApiEntryUsageEntryItem[] {
  const merged = new Map<PosApiEntryUsageEntry, PosApiEntryUsageCounts>(
    POS_API_ENTRY_USAGE_ENTRIES.map((entry) => [entry, { requests: 0, status4xx: 0, status5xx: 0 }]),
  )
  if (Array.isArray(raw)) {
    for (const item of raw) {
      if (!item || typeof item !== 'object') {
        continue
      }
      const record = item as Record<string, unknown>
      const target = merged.get(normalizePosApiEntryUsageEntry(record.entry))!
      const counts = normalizeCounts(record)
      target.requests += counts.requests
      target.status4xx += counts.status4xx
      target.status5xx += counts.status5xx
    }
  }
  const total = [...merged.values()].reduce((sum, counts) => sum + counts.requests, 0)
  return POS_API_ENTRY_USAGE_ENTRIES.map((entry) => {
    const counts = merged.get(entry)!
    // share 以规整后的数据为准重新计算，不信任后端传来的值（并入 unknown 后后端的 share 可能对不上）
    return { entry, ...counts, share: total > 0 ? counts.requests / total : 0 }
  })
}

function normalizeUsageBucket(raw: unknown): PosApiEntryUsageBucket | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }
  const record = raw as Record<string, unknown>
  const bucketStart = text(record.bucketStart)
  if (!bucketStart) {
    return null
  }
  return {
    bucketStart,
    bucketEnd: text(record.bucketEnd) || bucketStart,
    partial: record.partial === true,
    total: normalizeCounts(record.total),
    entries: normalizeUsageEntries(record.entries),
  }
}

function normalizeUsageStore(raw: unknown): PosApiEntryUsageStore | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }
  const record = raw as Record<string, unknown>
  return {
    storeCode: text(record.storeCode),
    storeName: text(record.storeName),
    total: normalizeCounts(record.total),
    entries: normalizeUsageEntries(record.entries),
  }
}

export function normalizePosApiEntryUsage(
  payload: unknown,
  query: PosApiEntryUsageQuery,
): PosApiEntryUsageResult {
  const record = (payload && typeof payload === 'object' ? payload : {}) as Record<string, unknown>
  const buckets = (Array.isArray(record.buckets) ? record.buckets : [])
    .map(normalizeUsageBucket)
    .filter((bucket): bucket is PosApiEntryUsageBucket => bucket !== null)
  const stores = (Array.isArray(record.stores) ? record.stores : [])
    .map(normalizeUsageStore)
    .filter((store): store is PosApiEntryUsageStore => store !== null)
  return {
    startDate: text(record.startDate) || query.startDate,
    endDate: text(record.endDate) || query.endDate,
    granularity: normalizeGranularity(record.granularity, query.granularity),
    timeZone: text(record.timeZone) || 'Australia/Sydney',
    storeCode: text(record.storeCode),
    total: normalizeCounts(record.total),
    entries: normalizeUsageEntries(record.entries),
    buckets,
    stores,
  }
}

/** 按日 / 周 / 月汇总各线路的请求数、4xx、5xx 与占比，另含分店明细；日期为悉尼日期。 */
export async function getPosApiEntryUsage(query: PosApiEntryUsageQuery): Promise<PosApiEntryUsageResult> {
  const response = await request.get<ApiResponse<unknown>>(`${POS_API_ENTRY_BASE}/usage`, {
    params: {
      startDate: query.startDate,
      endDate: query.endDate,
      granularity: query.granularity,
      ...(query.storeCode ? { storeCode: query.storeCode } : {}),
    },
  })
  return normalizePosApiEntryUsage(unwrapApiData(response), query)
}
