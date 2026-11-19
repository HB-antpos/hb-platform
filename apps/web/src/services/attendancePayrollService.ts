import type { ApiResponse } from '../types/api'
import type {
  AttendanceDeletedScheduleDto,
  AttendanceManualEntryPayload,
  AttendanceManualEntryPreview,
  AttendanceManualEntryResult,
  RevokeAttendanceManualEntryPayload,
  AttendanceHoursResultDto,
  AttendanceLeaveApprovalDto,
  AttendancePayCashBucketDto,
  AttendancePayPeriodStoreStatusDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
  AttendancePeriodKey,
  AttendancePeriodQuery,
  SaveAttendanceCashOverridePayload,
  SaveAttendanceLeaveApprovalPayload,
  SaveAttendancePayNotePayload,
  SaveAttendancePayRatePayload,
  UnlockAttendancePeriodPayload,
} from '../types/attendancePayroll'
import type {
  AttendanceApprovalDto,
  AttendancePagedResult,
  AttendanceQuery,
  ReviewAttendanceApprovalPayload,
} from '../types/scheduleAttendance'
import request from '../utils/request'
import {
  approveAttendanceApproval,
  getAttendanceApprovals,
  getPendingAttendanceApprovals,
  rejectAttendanceApproval,
  unwrapAttendanceApiData,
} from './scheduleAttendanceService'

// 工时与工资、审核工作台的接口（规格第 2 节，api/react/v1/attendance/...），
// 字段见后端 BlazorApp.Shared/DTOs/AttendancePayrollDtos.cs。后端返回 camelCase；
// DateTime 统一截成 YYYY-MM-DD，前端只按字符串比较、交给 dayjs 显示。
const API_BASE = '/api/react/v1/attendance'

type UnknownRecord = Record<string, unknown>

const dateOnly = (value: unknown): string => (typeof value === 'string' ? value.slice(0, 10) : '')
const optionalDate = (value: unknown): string | undefined => (typeof value === 'string' && value ? value.slice(0, 10) : undefined)

function asRecord(value: unknown): UnknownRecord {
  return value && typeof value === 'object' ? value as UnknownRecord : {}
}

/** 工时统计：日期规整；提醒的 date / otherStoreItems.date 同样处理。 */
export function normalizePayrollHours(value: unknown): AttendanceHoursResultDto {
  const data = asRecord(value) as unknown as AttendanceHoursResultDto
  return {
    ...data,
    periodStart: dateOnly(data.periodStart),
    periodEnd: optionalDate(data.periodEnd),
    weeks: data.weeks === 2 ? 2 : 1,
    pendingCount: data.pendingCount ?? 0,
    days: (data.days ?? []).map((day) => ({ ...day, date: dateOnly(day.date) })),
    employees: (data.employees ?? []).map((employee) => ({
      ...employee,
      birthday: optionalDate(employee.birthday),
      days: (employee.days ?? []).map((day) => ({
        ...day,
        date: dateOnly(day.date),
        pending: day.pending ?? [],
        approvedMarks: day.approvedMarks ?? [],
        otherStores: day.otherStores ?? [],
        missingPunch: day.missingPunch ?? false,
        manualEntry: day.manualEntry ?? null,
      })),
    })),
    alerts: (data.alerts ?? []).map((alert) => ({
      ...alert,
      date: optionalDate(alert.date),
      otherStoreItems: (alert.otherStoreItems ?? []).map((item) => ({ ...item, date: dateOnly(item.date) })),
    })),
  }
}

/** 后端总会返回 annual / cashOut（默认 0）；申请与核定都为 0 时视为没有，界面不显示核定框。 */
function presentAnnual(detail?: AttendanceLeaveApprovalDto): AttendanceLeaveApprovalDto | undefined {
  if (!detail) return undefined
  return detail.requestedHours > 0 || detail.approvedHours > 0 ? detail : undefined
}

/**
 * 公假 / 带薪假某一档的现金金额：后端总会返回；缺字段（例如 Web 先于后端上线的短暂窗口）时按「没有现金部分、不可改」兜底，
 * 此时界面显示 —，不会让人对着一个没有后端支持的格子手填。
 */
function normalizeCashBucket(value?: Partial<AttendancePayCashBucketDto>): AttendancePayCashBucketDto {
  return {
    hours: value?.hours ?? 0,
    amount: value?.amount ?? 0,
    systemAmount: value?.systemAmount ?? 0,
    isManual: value?.isManual ?? false,
    manualAmount: value?.manualAmount ?? null,
    editable: value?.editable ?? false,
    updatedBy: value?.updatedBy ?? null,
    updatedAt: value?.updatedAt ?? null,
  }
}

export function normalizePayrollPay(value: unknown): AttendancePayResultDto {
  const data = asRecord(value) as unknown as AttendancePayResultDto
  return {
    ...data,
    periodStart: dateOnly(data.periodStart),
    periodEnd: optionalDate(data.periodEnd),
    weeks: data.weeks === 2 ? 2 : 1,
    pendingCount: data.pendingCount ?? 0,
    rows: (data.rows ?? []).map((row) => ({
      ...row,
      cash: {
        ...row.cash,
        publicHoliday: normalizeCashBucket(row.cash?.publicHoliday),
        leave: normalizeCashBucket(row.cash?.leave),
      },
      leaveDetail: {
        sick: row.leaveDetail?.sick ?? 0,
        publicHolidayPay: row.leaveDetail?.publicHolidayPay ?? 0,
        annual: presentAnnual(row.leaveDetail?.annual),
        cashOut: presentAnnual(row.leaveDetail?.cashOut),
      },
      warnings: row.warnings ?? [],
    })),
  }
}

export function normalizePayRate(value: unknown): AttendancePayRateDto {
  const rate = asRecord(value) as unknown as AttendancePayRateDto
  return {
    ...rate,
    birthday: optionalDate(rate.birthday),
    birthdayReviewDate: optionalDate(rate.birthdayReviewDate),
    effectiveFrom: optionalDate(rate.effectiveFrom),
    nextEffectiveFrom: optionalDate(rate.nextEffectiveFrom),
    cashAllowancePerPeriod: rate.cashAllowancePerPeriod ?? 0,
  }
}

// ===== 工时统计 / 工资表 =====

export async function getAttendancePayrollHours(query: AttendancePeriodQuery): Promise<AttendanceHoursResultDto> {
  const response = await request.get<ApiResponse<AttendanceHoursResultDto>>(`${API_BASE}/payroll/hours`, {
    params: query as unknown as Record<string, unknown>,
  })
  return normalizePayrollHours(unwrapAttendanceApiData(response))
}

export async function getAttendancePayrollPay(query: AttendancePeriodQuery): Promise<AttendancePayResultDto> {
  const response = await request.get<ApiResponse<AttendancePayResultDto>>(`${API_BASE}/payroll/pay`, {
    params: query as unknown as Record<string, unknown>,
  })
  return normalizePayrollPay(unwrapAttendanceApiData(response))
}

// ===== 个人时薪表 =====

/** 按哪一天取适用时薪：传所看工资周期的起始日；不传按分店今天。 */
export async function getAttendancePayRates(params: {
  storeCode: string
  keyword?: string
  asOf?: string
}): Promise<AttendancePayRateDto[]> {
  const response = await request.get<ApiResponse<AttendancePayRateDto[]>>(`${API_BASE}/payroll/rates`, { params })
  return (unwrapAttendanceApiData<unknown[]>(response) ?? []).map(normalizePayRate)
}

/** 分店侧栏的「🔒 / 未锁」：没有权限的分店后端会略过。 */
export async function getAttendancePayPeriodStatuses(params: {
  storeCodes: readonly string[]
  periodStart: string
  weeks: 1 | 2
}): Promise<AttendancePayPeriodStoreStatusDto[]> {
  const response = await request.get<ApiResponse<AttendancePayPeriodStoreStatusDto[]>>(`${API_BASE}/payroll/period-statuses`, {
    params: { storeCodes: params.storeCodes.join(','), periodStart: params.periodStart, weeks: params.weeks },
  })
  return (unwrapAttendanceApiData<unknown[]>(response) ?? []).map((item) => {
    const row = asRecord(item)
    return { storeCode: String(row.storeCode ?? ''), locked: row.locked === true }
  })
}

export async function getAttendancePayRateHistory(userGuid: string, storeCode: string): Promise<AttendancePayRateDto[]> {
  const response = await request.get<ApiResponse<AttendancePayRateDto[]>>(
    `${API_BASE}/payroll/rates/${encodeURIComponent(userGuid)}/history`,
    { params: { storeCode } },
  )
  return (unwrapAttendanceApiData<unknown[]>(response) ?? []).map(normalizePayRate)
}

/** 只新增一条「生效记录」，不改历史。 */
export async function createAttendancePayRate(payload: SaveAttendancePayRatePayload): Promise<AttendancePayRateDto> {
  const response = await request.post<ApiResponse<AttendancePayRateDto>>(`${API_BASE}/payroll/rates`, payload)
  return normalizePayRate(unwrapAttendanceApiData(response))
}

// ===== 期操作（periodStart 必须用后端返回的期起点）=====

export async function saveAttendancePayNote(payload: SaveAttendancePayNotePayload): Promise<void> {
  unwrapAttendanceApiData(await request.put<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/notes`, payload))
}

export async function saveAttendanceLeaveApproval(payload: SaveAttendanceLeaveApprovalPayload): Promise<void> {
  unwrapAttendanceApiData(await request.put<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/leave-approvals`, payload))
}

/** 手填 / 清除某人某期「公假」「带薪假」的现金金额（覆盖该档系统值）；amount 为 null = 恢复系统值。 */
export async function saveAttendanceCashOverride(payload: SaveAttendanceCashOverridePayload): Promise<void> {
  unwrapAttendanceApiData(await request.put<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/cash-overrides`, payload))
}

// ===== 手动补录工时（店长 / Admin 为「当天没有任何打卡」的员工补整天）=====

/** 预览：不写库，返回工时、是否会新建排班与版本号；校验失败时抛出带后端提示的错误。 */
export async function previewAttendanceManualEntry(payload: AttendanceManualEntryPayload): Promise<AttendanceManualEntryPreview> {
  return unwrapAttendanceApiData<AttendanceManualEntryPreview>(
    await request.post<ApiResponse<unknown>>(`${API_BASE}/managed/manual-entries/preview`, payload),
  )
}

/** 保存：payload.previewRevision 必须是刚预览返回的值，否则后端要求重新预览。 */
export async function createAttendanceManualEntry(payload: AttendanceManualEntryPayload): Promise<AttendanceManualEntryResult> {
  return unwrapAttendanceApiData<AttendanceManualEntryResult>(
    await request.post<ApiResponse<unknown>>(`${API_BASE}/managed/manual-entries`, payload),
  )
}

/** 撤销某员工某天的补录（补录新建的排班一并取消）。 */
export async function revokeAttendanceManualEntry(payload: RevokeAttendanceManualEntryPayload): Promise<void> {
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(`${API_BASE}/managed/manual-entries/revoke`, payload))
}

/** 提交并锁定：后端校验本期待处理为 0（PERIOD_HAS_PENDING），锁定后各写接口返回 PERIOD_LOCKED。 */
export async function submitAttendancePayPeriod(payload: AttendancePeriodKey): Promise<void> {
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/submit`, payload))
}

export async function unlockAttendancePayPeriod(payload: UnlockAttendancePeriodPayload): Promise<void> {
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/unlock`, payload))
}

// ===== 已删除排班（后端返回 AttendanceScheduleDto 列表，带 deletedAt / deletedBy，不带班段）=====

export async function getDeletedAttendanceSchedules(params: {
  storeCode: string
  fromDate: string
  toDate: string
}): Promise<AttendanceDeletedScheduleDto[]> {
  const response = await request.get<ApiResponse<unknown>>(`${API_BASE}/schedules/deleted`, {
    params: { ...params, page: 1, pageSize: 200 },
  })
  const data = unwrapAttendanceApiData<unknown>(response)
  const items = Array.isArray(data) ? data : (asRecord(data).items as unknown[] | undefined) ?? []
  return items.map((item) => {
    const row = asRecord(item)
    return {
      scheduleGuid: String(row.scheduleGuid ?? ''),
      storeCode: String(row.storeCode ?? ''),
      userGuid: String(row.userGuid ?? ''),
      userName: (row.userName ?? row.employeeName) as string | undefined,
      workDate: dateOnly(row.workDate),
      startTime: String(row.startTime ?? '').slice(0, 5),
      endTime: String(row.endTime ?? '').slice(0, 5),
      deletedAt: row.deletedAt as string | undefined,
      deletedBy: row.deletedBy as string | undefined,
    }
  })
}

/** 恢复软删除的排班；后端恢复后重新对账派生审批。 */
export async function restoreAttendanceSchedule(scheduleGuid: string): Promise<void> {
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(
    `${API_BASE}/schedules/${encodeURIComponent(scheduleGuid)}/restore`,
  ))
}

// ===== 审核工作台 =====

export type AttendanceReviewView = 'pending' | 'processed' | 'self' | 'all'

/**
 * 审核列表：待审核走 approvals/pending（打开时后端先对账近 14 天排班、生成带薪公假确认）；
 * 已处理 / 全部走 approvals；本人审批记录带 isSelfReview=true。后端返回整列数组，前端不分页。
 */
export async function getAttendanceReviewQueue(
  view: AttendanceReviewView,
  query: AttendanceQuery,
): Promise<AttendancePagedResult<AttendanceApprovalDto>> {
  if (view === 'pending') return getPendingAttendanceApprovals(query)
  if (view === 'self') {
    return getAttendanceApprovals({ ...query, isSelfReview: true } as AttendanceQuery)
  }
  const result = await getAttendanceApprovals(query)
  if (view !== 'processed') return result
  // 后端只支持单个 reviewStatus 筛选，「已处理」在前端去掉待审核
  const items = result.items.filter((item) => item.reviewStatus !== 'Pending')
  return { ...result, items, total: items.length }
}

/** 菜单角标：直接按状态查待审（不走 pending，避免每次切页都触发对账）。 */
export async function getAttendancePendingCount(): Promise<number> {
  const result = await getAttendanceApprovals({ reviewStatus: 'Pending', page: 1, pageSize: 1 } as AttendanceQuery)
  return result.total
}

export async function reviewAttendanceApproval(
  approvalGuid: string,
  action: 'approve' | 'reject',
  payload: ReviewAttendanceApprovalPayload,
): Promise<AttendanceApprovalDto> {
  return action === 'approve'
    ? approveAttendanceApproval(approvalGuid, payload)
    : rejectAttendanceApproval(approvalGuid, payload)
}
