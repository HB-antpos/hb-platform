import { apiClient } from "@/shared/api/client";
import { arr, normalizeLegacyLogItem, normalizeLegacyLogReview, num, str, strings, unwrap, type Raw } from "./api-normalization";
import type { LegacyEmployeeSummaryResult, LegacyLogContext, LegacyLogPage, LegacyLogReview } from "./types";

const BASE_URL = "/react/legacy-employee-logs";

export async function fetchLegacyLogs(params: URLSearchParams, signal?: AbortSignal): Promise<LegacyLogPage> {
  const response = await apiClient.get(BASE_URL, { params, signal });
  const data = unwrap(response.data);
  const summary = (data.riskSummary ?? {}) as Raw;
  return {
    items: arr(data.items, normalizeLegacyLogItem),
    total: num(data.total),
    operationCounts: arr(data.operationCounts, (row) => ({ operation: str(row.operation), count: num(row.count) })),
    employees: arr(data.employees, (row) => ({ employeeId: str(row.employeeId), employeeName: str(row.employeeName), count: num(row.count) })),
    devices: arr(data.devices, (row) => ({ deviceCode: str(row.deviceCode), count: num(row.count) })),
    riskSummary: {
      dangerTotal: num(summary.dangerTotal),
      abnormalTotal: num(summary.abnormalTotal),
      pendingReview: num(summary.pendingReview),
      abnormalEmployees: num(summary.abnormalEmployees),
      abnormalByRule: arr(summary.abnormalByRule, (row) => ({ ruleCode: str(row.ruleCode) ?? "", count: num(row.count) })),
    },
  };
}

export async function fetchLegacyLogContext(id: string, signal?: AbortSignal): Promise<LegacyLogContext> {
  // 旧数据的编号不保证是规范 GUID，放在查询串里传；异常核查看 ±15 分钟上下文。
  const response = await apiClient.get(`${BASE_URL}/context`, { params: { id, windowMinutes: 15 }, signal });
  const data = unwrap(response.data);
  const target = normalizeLegacyLogItem((data.target ?? {}) as Raw);
  if (target.id !== id) {
    // 串台会把别的记录显示在当前详情页，宁可报错。
    throw Object.assign(new Error("Legacy log context mismatch"), { code: "LEGACY_LOG_INVALID_RESPONSE" });
  }
  return {
    target,
    windowMinutes: num(data.windowMinutes) || 15,
    neighbors: arr(data.neighbors, normalizeLegacyLogItem),
    truncated: data.truncated === true,
  };
}

export async function fetchLegacyEmployeeSummary(params: URLSearchParams, signal?: AbortSignal): Promise<LegacyEmployeeSummaryResult> {
  const response = await apiClient.get(`${BASE_URL}/employee-summary`, { params, signal });
  const data = unwrap(response.data);
  return {
    total: num(data.total),
    dangerTotal: num(data.dangerTotal),
    employees: arr(data.employees, (row) => ({
      employeeId: str(row.employeeId),
      employeeName: str(row.employeeName),
      storeCodes: strings(row.storeCodes),
      deviceCodes: strings(row.deviceCodes),
      total: num(row.total),
      dangerCount: num(row.dangerCount),
      abnormalCount: num(row.abnormalCount),
      pendingReview: num(row.pendingReview),
      abnormalByRule: arr(row.abnormalByRule, (rule) => ({ ruleCode: str(rule.ruleCode) ?? "", count: num(rule.count) })),
      amountImpact: num(row.amountImpact),
    })),
  };
}

/** 核查：版本号不符时后端返回 409（REVIEW_CONFLICT）。 */
export async function reviewLegacyLog(body: {
  logId: string;
  result: "normal" | "followUp" | "revoked";
  note?: string;
  expectedVersion: number | null;
}): Promise<LegacyLogReview> {
  const response = await apiClient.post(`${BASE_URL}/reviews`, body);
  const review = normalizeLegacyLogReview(unwrap(response.data));
  if (!review) {
    throw Object.assign(new Error("Legacy log review response invalid"), { code: "LEGACY_LOG_INVALID_RESPONSE" });
  }
  return review;
}
