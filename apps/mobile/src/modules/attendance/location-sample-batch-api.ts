import { apiClient } from "@/shared/api/client";
import {
  normalizeLocationSampleBatchResult,
  type AttendanceLocationSampleBatchRequest,
  type AttendanceLocationSampleBatchResult,
} from "@/modules/attendance/location-sample-batch";

// 与 attendance/api.ts 的 ATTENDANCE_BASE 保持一致；该文件有未提交改动，这里不去导出它，单独声明。
const ATTENDANCE_BASE = "/react/v1/attendance";

/**
 * 批量上传班中定位点：POST /react/v1/attendance/location-samples/batch。
 * - 沿用单点接口的 X-Skip-Auth-Redirect：后台上传遇到 401 不拉起登录页，只清理本地会话；
 * - 响应经 normalizeLocationSampleBatchResult 严格校验，格式异常会抛错，调用方据此保留整批重试；
 * - signal 用于下班停止流程的超时放弃。
 */
export async function postAttendanceLocationSamplesBatch(
  request: AttendanceLocationSampleBatchRequest,
  options?: { signal?: AbortSignal },
): Promise<AttendanceLocationSampleBatchResult> {
  const response = await apiClient.post(
    `${ATTENDANCE_BASE}/location-samples/batch`,
    request,
    {
      headers: { "X-Skip-Auth-Redirect": "1" },
      ...(options?.signal ? { signal: options.signal } : {}),
    },
  );
  return normalizeLocationSampleBatchResult(response.data);
}
