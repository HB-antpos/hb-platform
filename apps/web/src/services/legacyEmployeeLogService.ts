import type { ApiResponse } from '../types/api'
import type {
  LegacyEmployeeLogContext,
  LegacyEmployeeLogListResult,
  LegacyEmployeeLogQueryParams,
} from '../types/legacyEmployeeLog'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/legacy-employee-logs'

export async function getLegacyEmployeeLogs(params: LegacyEmployeeLogQueryParams, signal?: AbortSignal) {
  // 数组参数由 request 按重复键展开（employeeIds=a&employeeIds=b），与后端 List<string> 绑定一致。
  const response = await request.get<ApiResponse<LegacyEmployeeLogListResult>>(API_BASE, {
    params: params as unknown as Record<string, unknown>,
    signal,
  })
  return unwrapApiData(response)
}

export async function getLegacyEmployeeLogContext(id: string) {
  // 旧数据的编号不保证是规范 GUID，放在查询串里传。
  const response = await request.get<ApiResponse<LegacyEmployeeLogContext>>(`${API_BASE}/context`, {
    params: { id },
  })
  return unwrapApiData(response)
}
