import type { ApiResponse } from '../types/api'
import type {
  PosApiEntryDeviceStats,
  PosApiEntryMode,
  PosApiEntryStoreItem,
  PosApiEntryUpdateResult,
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
