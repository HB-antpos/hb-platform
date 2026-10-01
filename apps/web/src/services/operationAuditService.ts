import { normalizeOperationAuditPage } from '../pages/PosAdmin/OperationLogs/operationLogsLogic'
import type { ApiResponse, PagedResult } from '../types/api'
import type { LegacyEmployeeLogReview } from '../types/legacyEmployeeLog'
import type {
  OperationAuditContext,
  OperationAuditDetail,
  OperationAuditEmployeeSummaryResult,
  OperationAuditListItem,
  OperationAuditQueryParams,
  OperationAuditReviewRequest,
  OperationAuditSummary,
} from '../types/operationAudit'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/pos-operation-audits'

export async function getOperationAudits(params: OperationAuditQueryParams) {
  const response = await request.get<ApiResponse<PagedResult<OperationAuditListItem>>>(API_BASE, {
    params: params as unknown as Record<string, unknown>,
  })
  return normalizeOperationAuditPage(unwrapApiData(response))
}

export async function getOperationAuditDetail(eventId: string) {
  const response = await request.get<ApiResponse<OperationAuditDetail>>(
    `${API_BASE}/${encodeURIComponent(eventId)}`,
  )
  return unwrapApiData(response)
}

/** 汇总计数（风险入口基数、待核查数）；与列表共用查询条件。 */
export async function getOperationAuditSummary(params: OperationAuditQueryParams) {
  const response = await request.get<ApiResponse<OperationAuditSummary>>(`${API_BASE}/summary`, {
    params: params as unknown as Record<string, unknown>,
  })
  return unwrapApiData(response)
}

/** 按收银员汇总：只用分店、时间、设备与关键字等基础条件。 */
export async function getOperationAuditEmployeeSummary(params: OperationAuditQueryParams) {
  const response = await request.get<ApiResponse<OperationAuditEmployeeSummaryResult>>(`${API_BASE}/employee-summary`, {
    params: params as unknown as Record<string, unknown>,
  })
  return unwrapApiData(response)
}

export async function getOperationAuditContext(eventId: string, windowMinutes?: 5 | 10 | 15) {
  const response = await request.get<ApiResponse<OperationAuditContext>>(
    `${API_BASE}/${encodeURIComponent(eventId)}/context`,
    { params: windowMinutes ? { windowMinutes } : undefined },
  )
  return unwrapApiData(response)
}

/** 核查：版本号不符时后端返回 409（REVIEW_CONFLICT），调用方应刷新后重试。 */
export async function reviewOperationAudit(body: OperationAuditReviewRequest) {
  const response = await request.post<ApiResponse<LegacyEmployeeLogReview>>(`${API_BASE}/reviews`, body)
  return unwrapApiData(response)
}
