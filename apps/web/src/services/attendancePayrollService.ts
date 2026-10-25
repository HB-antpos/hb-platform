import type { ApiResponse } from '../types/api'
import type {
  AttendanceDeletedScheduleDto,
  AttendanceHoursResultDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
  AttendancePeriodKey,
  AttendancePeriodQuery,
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

// 工时与工资、审核工作台的接口（规格第 2 节，api/react/v1/attendance/...）。
// 后端卡 A / 卡 B 合入前，开发时设 VITE_ATTENDANCE_MOCK=1 走本地 mock；
// 该变量在生产构建里不存在，mock 分支与 mock 模块会被整体摇树掉，不进任何产物。
const API_BASE = '/api/react/v1/attendance'
const USE_MOCK = import.meta.env.VITE_ATTENDANCE_MOCK === '1'

function loadMock() {
  return import('./attendancePayrollMock')
}

// ===== 工时统计 / 工资表 =====

export async function getAttendancePayrollHours(query: AttendancePeriodQuery): Promise<AttendanceHoursResultDto> {
  if (USE_MOCK) return (await loadMock()).mockGetHours(query)
  const response = await request.get<ApiResponse<AttendanceHoursResultDto>>(`${API_BASE}/payroll/hours`, {
    params: query as unknown as Record<string, unknown>,
  })
  return unwrapAttendanceApiData<AttendanceHoursResultDto>(response)
}

export async function getAttendancePayrollPay(query: AttendancePeriodQuery): Promise<AttendancePayResultDto> {
  if (USE_MOCK) return (await loadMock()).mockGetPay(query)
  const response = await request.get<ApiResponse<AttendancePayResultDto>>(`${API_BASE}/payroll/pay`, {
    params: query as unknown as Record<string, unknown>,
  })
  return unwrapAttendanceApiData<AttendancePayResultDto>(response)
}

// ===== 个人时薪表 =====

export async function getAttendancePayRates(params: { storeCode: string; keyword?: string }): Promise<AttendancePayRateDto[]> {
  if (USE_MOCK) return (await loadMock()).mockGetRates(params)
  const response = await request.get<ApiResponse<AttendancePayRateDto[]>>(`${API_BASE}/payroll/rates`, { params })
  return unwrapAttendanceApiData<AttendancePayRateDto[]>(response) ?? []
}

export async function getAttendancePayRateHistory(userGuid: string): Promise<AttendancePayRateDto[]> {
  if (USE_MOCK) return (await loadMock()).mockGetRateHistory(userGuid)
  const response = await request.get<ApiResponse<AttendancePayRateDto[]>>(
    `${API_BASE}/payroll/rates/${encodeURIComponent(userGuid)}/history`,
  )
  return unwrapAttendanceApiData<AttendancePayRateDto[]>(response) ?? []
}

/** 只新增一条「生效记录」，不改历史。 */
export async function createAttendancePayRate(payload: SaveAttendancePayRatePayload): Promise<AttendancePayRateDto> {
  if (USE_MOCK) return (await loadMock()).mockCreateRate(payload)
  const response = await request.post<ApiResponse<AttendancePayRateDto>>(`${API_BASE}/payroll/rates`, payload)
  return unwrapAttendanceApiData<AttendancePayRateDto>(response)
}

// ===== 期操作 =====

export async function saveAttendancePayNote(payload: SaveAttendancePayNotePayload): Promise<void> {
  if (USE_MOCK) return (await loadMock()).mockSaveNote(payload)
  unwrapAttendanceApiData(await request.put<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/notes`, payload))
}

export async function saveAttendanceLeaveApproval(payload: SaveAttendanceLeaveApprovalPayload): Promise<void> {
  if (USE_MOCK) return (await loadMock()).mockSaveLeaveApproval(payload)
  unwrapAttendanceApiData(await request.put<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/leave-approvals`, payload))
}

/** 提交并锁定：后端校验本期待处理为 0，锁定后各写接口返回 PERIOD_LOCKED。 */
export async function submitAttendancePayPeriod(payload: AttendancePeriodKey): Promise<void> {
  if (USE_MOCK) return (await loadMock()).mockSubmitPeriod(payload)
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/submit`, payload))
}

export async function unlockAttendancePayPeriod(payload: UnlockAttendancePeriodPayload): Promise<void> {
  if (USE_MOCK) return (await loadMock()).mockUnlockPeriod(payload)
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(`${API_BASE}/payroll/periods/unlock`, payload))
}

// ===== 已删除排班 =====

export async function getDeletedAttendanceSchedules(params: {
  storeCode: string
  fromDate: string
  toDate: string
}): Promise<AttendanceDeletedScheduleDto[]> {
  if (USE_MOCK) return (await loadMock()).mockGetDeletedSchedules(params)
  const response = await request.get<ApiResponse<AttendanceDeletedScheduleDto[]>>(`${API_BASE}/schedules/deleted`, { params })
  const data = unwrapAttendanceApiData<unknown>(response)
  // 兼容后端直接返回数组或分页对象
  if (Array.isArray(data)) return data as AttendanceDeletedScheduleDto[]
  const items = (data as { items?: AttendanceDeletedScheduleDto[] } | null)?.items
  return items ?? []
}

/** 恢复软删除的排班；后端恢复后重新对账派生审批。 */
export async function restoreAttendanceSchedule(scheduleGuid: string): Promise<void> {
  if (USE_MOCK) return (await loadMock()).mockRestoreSchedule(scheduleGuid)
  unwrapAttendanceApiData(await request.post<ApiResponse<unknown>>(
    `${API_BASE}/schedules/${encodeURIComponent(scheduleGuid)}/restore`,
  ))
}

// ===== 审核工作台 =====

export type AttendanceReviewView = 'pending' | 'processed' | 'self' | 'all'

/**
 * 审核列表：待审核走 approvals/pending（打开时后端先对账近 14 天排班）；
 * 已处理 / 全部走 approvals；本人审批记录带 selfReviewOnly=true（卡 A 新增筛选）。
 */
export async function getAttendanceReviewQueue(
  view: AttendanceReviewView,
  query: AttendanceQuery,
): Promise<AttendancePagedResult<AttendanceApprovalDto>> {
  if (USE_MOCK) return (await loadMock()).mockGetReviewQueue(view, query)
  if (view === 'pending') return getPendingAttendanceApprovals(query)
  if (view === 'self') {
    return getAttendanceApprovals({ ...query, selfReviewOnly: true } as AttendanceQuery)
  }
  const result = await getAttendanceApprovals(query)
  if (view !== 'processed') return result
  // 后端只支持单个 reviewStatus 筛选，「已处理」在前端去掉待审核
  const items = result.items.filter((item) => item.reviewStatus !== 'Pending')
  return { ...result, items }
}

/** 菜单角标用：只取待审总数（pageSize=1）。 */
export async function getAttendancePendingCount(): Promise<number> {
  if (USE_MOCK) return (await loadMock()).mockGetPendingCount()
  const result = await getPendingAttendanceApprovals({ page: 1, pageSize: 1 })
  return result.total
}

export async function reviewAttendanceApproval(
  approvalGuid: string,
  action: 'approve' | 'reject',
  payload: ReviewAttendanceApprovalPayload,
): Promise<AttendanceApprovalDto> {
  if (USE_MOCK) return (await loadMock()).mockReviewApproval(approvalGuid, action, payload)
  return action === 'approve'
    ? approveAttendanceApproval(approvalGuid, payload)
    : rejectAttendanceApproval(approvalGuid, payload)
}
