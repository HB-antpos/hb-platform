import type { ApiResponse } from '../types/api'
import type {
  MyobPayRateSyncApplyPayload,
  MyobPayRateSyncApplyResult,
  MyobPayRateSyncCell,
  MyobPayRateSyncCellKey,
  MyobPayRateSyncKind,
  MyobPayRateSyncPreview,
  MyobPayRateSyncRow,
} from '../types/myobPayRateSync'
import request, { unwrapApiData } from '../utils/request'

// 后端控制器 MyobPayRateSyncController：类级同时要求 Attendance.Payroll.Edit 与 Myob.Employee.Bind（两个策略是「且」）。
// 只读 MYOB、只往我方时薪表新增记录；响应不含 MYOB 员工 UID / 年薪。
const BASE = '/api/react/v1/myob/pay-rate-sync'

const KINDS: readonly MyobPayRateSyncKind[] = ['Create', 'Fill', 'Differ', 'Same', 'Skip']
const CELL_KEYS: readonly MyobPayRateSyncCellKey[] = ['weekday', 'saturday', 'sunday', 'publicHoliday']

function text(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function nullableText(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : null
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

/** 时薪只认有限数字，其余按 null：界面显示「—」，绝不当 0 处理。 */
function nullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function dateOnly(value: unknown): string {
  return typeof value === 'string' ? value.slice(0, 10) : ''
}

function cellKeys(value: unknown): MyobPayRateSyncCellKey[] {
  return Array.isArray(value) ? value.filter((item): item is MyobPayRateSyncCellKey => CELL_KEYS.includes(item as MyobPayRateSyncCellKey)) : []
}

export function normalizePayRateSyncCell(raw: unknown): MyobPayRateSyncCell {
  const item = record(raw)
  return {
    ourRate: nullableNumber(item.ourRate),
    suggested: nullableNumber(item.suggested),
    multiplier: nullableNumber(item.multiplier),
    // 没有建议值就不可能有东西可写，后端字段异常时也不放行
    canApply: item.canApply === true && nullableNumber(item.suggested) !== null,
    unavailableReason: nullableText(item.unavailableReason),
    defaultSelected: item.defaultSelected === true,
  }
}

export function normalizePayRateSyncRow(raw: unknown): MyobPayRateSyncRow | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) return null
  // 不认识的类别按 Skip 处理：偏保守，绝不会因为后端新增类别而误写
  const kind = KINDS.find((candidate) => candidate === item.kind) ?? 'Skip'
  return {
    userGuid,
    userName: text(item.userName),
    employmentType: nullableText(item.employmentType),
    kind,
    skipReason: nullableText(item.skipReason),
    payMode: nullableText(item.payMode),
    myobHourlyRate: nullableNumber(item.myobHourlyRate),
    rounded: item.rounded === true,
    weekday: normalizePayRateSyncCell(item.weekday),
    saturday: normalizePayRateSyncCell(item.saturday),
    sunday: normalizePayRateSyncCell(item.sunday),
    publicHoliday: normalizePayRateSyncCell(item.publicHoliday),
    hash: text(item.hash),
  }
}

export function normalizePayRateSyncPreview(payload: unknown): MyobPayRateSyncPreview {
  const data = record(payload)
  return {
    connectionGuid: text(data.connectionGuid),
    connectionName: text(data.connectionName),
    storeCode: text(data.storeCode),
    effectiveFrom: dateOnly(data.effectiveFrom),
    fetchedAt: text(data.fetchedAt),
    notBoundCount: typeof data.notBoundCount === 'number' ? data.notBoundCount : 0,
    rows: Array.isArray(data.rows)
      ? data.rows.map(normalizePayRateSyncRow).filter((row): row is MyobPayRateSyncRow => row !== null)
      : [],
  }
}

export function normalizePayRateSyncApplyResult(payload: unknown): MyobPayRateSyncApplyResult {
  const data = record(payload)
  return {
    effectiveFrom: dateOnly(data.effectiveFrom),
    written: (Array.isArray(data.written) ? data.written : []).map((raw) => {
      const item = record(raw)
      return { userGuid: text(item.userGuid), userName: text(item.userName), rateGuid: text(item.rateGuid), cells: cellKeys(item.cells) }
    }),
    failed: (Array.isArray(data.failed) ? data.failed : []).map((raw) => {
      const item = record(raw)
      return { userGuid: text(item.userGuid), userName: text(item.userName), code: text(item.code), message: text(item.message) }
    }),
  }
}

/** 预览：读 MYOB（10 分钟缓存，refresh=true 绕过）并推算每个员工每一格的建议值。 */
export async function previewMyobPayRateSync(params: {
  connectionGuid: string
  storeCode: string
  effectiveFrom?: string
  refresh?: boolean
}): Promise<MyobPayRateSyncPreview> {
  const response = await request.post<ApiResponse<unknown>>(`${BASE}/preview`, {
    connectionGuid: params.connectionGuid,
    storeCode: params.storeCode,
    effectiveFrom: params.effectiveFrom || undefined,
    refresh: params.refresh === true,
  })
  return normalizePayRateSyncPreview(unwrapApiData(response))
}

/** 应用：后端会重新读 MYOB 并校验哈希，逐员工写入，个别失败不影响其他人。 */
export async function applyMyobPayRateSync(payload: MyobPayRateSyncApplyPayload): Promise<MyobPayRateSyncApplyResult> {
  const response = await request.post<ApiResponse<unknown>>(`${BASE}/apply`, payload)
  return normalizePayRateSyncApplyResult(unwrapApiData(response))
}
