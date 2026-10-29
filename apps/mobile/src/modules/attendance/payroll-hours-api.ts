// 店长「工时统计」接口：GET /react/v1/attendance/payroll/hours（规格第 2 节）。
// apiClient 拦截器已解包 ApiResponse 信封；归一化在 payroll-hours.ts（纯函数，有测试）。
import { apiClient } from "@/shared/api/client";
import { normalizePayrollHoursReport } from "./payroll-hours";
import type { PayrollHoursQuery, PayrollHoursReport } from "./payroll-hours-types";

export const PAYROLL_HOURS_PATH = "/react/v1/attendance/payroll/hours";

/** react-query 缓存键：列表与单人明细共用同一份数据（明细从全店结果里取这个人）。 */
export function payrollHoursQueryKey(query: PayrollHoursQuery) {
  return [
    "attendance",
    "payroll",
    "hours",
    query.storeCode,
    query.periodStart ?? "current",
    query.weeks,
    Boolean(query.includePending),
    Boolean(query.mergeStores),
  ] as const;
}

export async function getPayrollHours(query: PayrollHoursQuery): Promise<PayrollHoursReport> {
  const response = await apiClient.get(PAYROLL_HOURS_PATH, {
    params: {
      storeCode: query.storeCode,
      // 不传 periodStart 时由后端取当前期（2 周时与「双周起始日」对齐）。
      periodStart: query.periodStart || undefined,
      weeks: query.weeks,
      includePending: query.includePending || undefined,
      mergeStores: query.mergeStores || undefined,
    },
  });
  return normalizePayrollHoursReport(response.data, query);
}
