import type { EmployeeProfileReviewStatusFilter } from "./types";

// C1 审核列表的分段与搜索条件：分段决定后端 status，搜索只做首尾去空格。

export type EmployeeProfileReviewSegment = "pending" | "processed";

export const EMPLOYEE_PROFILE_REVIEW_SEGMENTS: readonly EmployeeProfileReviewSegment[] = ["pending", "processed"];

/** 搜索输入停止 300ms 后才发请求，避免逐字查询。 */
export const EMPLOYEE_PROFILE_REVIEW_SEARCH_DEBOUNCE_MS = 300;

export function getEmployeeProfileReviewStatusFilter(
  segment: EmployeeProfileReviewSegment
): EmployeeProfileReviewStatusFilter {
  return segment === "processed" ? "Processed" : "Pending";
}

/**
 * 待审核分段「等得最久的在前」（后端 Sort=oldest）；已处理分段沿用后端默认「最近处理 / 提交的在前」。
 * 旧后端不认识 sort 参数会忽略它，只是顺序仍为最新在前，不会报错。
 */
export function getEmployeeProfileReviewSort(
  segment: EmployeeProfileReviewSegment
): "oldest" | undefined {
  return segment === "pending" ? "oldest" : undefined;
}

export function normalizeEmployeeProfileReviewSearch(value: string | null | undefined) {
  return value?.trim().slice(0, 100) ?? "";
}

/** 查询键保持在 ["employeeProfileReview", "requests"] 前缀下，权限失效时可被整体清理。 */
export function getEmployeeProfileReviewListQueryKey(
  segment: EmployeeProfileReviewSegment,
  search: string
) {
  return [
    "employeeProfileReview",
    "requests",
    getEmployeeProfileReviewStatusFilter(segment),
    normalizeEmployeeProfileReviewSearch(search),
  ] as const;
}
