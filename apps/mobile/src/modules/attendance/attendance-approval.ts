import type { AttendanceApprovalPayload } from "./types";

export type OvertimeApprovalAction = "approve" | "reject";

export type KnownAttendanceApprovalSourceType =
  | "Punch"
  | "Leave"
  | "PunchAdjustment"
  | "Overtime"
  | "MissingClockOut"
  | "MealBreak"
  | "PublicHolidayPay"
  | "NoShow";

export type OvertimeApprovalValidationError =
  | "outOfRange"
  | "invalidIncrement"
  | "remarkRequired";

const knownSourceTypes = new Set<KnownAttendanceApprovalSourceType>([
  "Punch",
  "Leave",
  "PunchAdjustment",
  "Overtime",
  "MissingClockOut",
  "MealBreak",
  // 带薪公假确认（卡 B）：批准体带 approvedPaidHours，拒绝＝不适用。
  "PublicHolidayPay",
  // 缺勤（有排班但整班没打卡）：只能「确认缺勤」，后端不允许驳回。
  "NoShow",
]);

export function isKnownAttendanceApprovalSourceType(
  value: string,
): value is KnownAttendanceApprovalSourceType {
  return knownSourceTypes.has(value as KnownAttendanceApprovalSourceType);
}

/** 缺勤待审不能驳回：卡片 / 详情都不显示驳回按钮（后端驳回返回 NO_SHOW_REJECT_NOT_ALLOWED）。 */
export function canRejectAttendanceApproval(sourceType: string) {
  return sourceType !== "NoShow";
}

/** 审核接口错误码 → 文案 key（attendance:approvals.board.*）；不认识的返回 undefined，沿用后端 message。 */
const APPROVAL_ERROR_KEYS: Record<string, string> = {
  NO_SHOW_RESOLVED: "approvals.board.noShow.resolvedError",
  NO_SHOW_REJECT_NOT_ALLOWED: "approvals.board.noShow.rejectNotAllowed",
};

export function getAttendanceApprovalErrorKey(errorCode?: string) {
  return errorCode ? APPROVAL_ERROR_KEYS[errorCode] : undefined;
}

/** 这些错误码表示后端已自动取消该待审：提示后要刷新列表，让卡片消失。 */
export function shouldRefreshAfterApprovalError(errorCode?: string) {
  return errorCode === "NO_SHOW_RESOLVED";
}

export function getSupplementalAttendanceApprovalDetail(input: {
  sourceType: string;
  detail?: string;
  displayedTitle: string;
}): string | undefined {
  if (input.sourceType !== "Punch" && input.sourceType !== "Leave") return undefined;
  const detail = input.detail?.trim();
  if (!detail || detail === input.displayedTitle.trim()) return undefined;
  return detail;
}

export function buildAttendanceApprovalReviewRequest(
  payload: AttendanceApprovalPayload,
) {
  return {
    reviewRemark: payload.remark?.trim() || undefined,
    ...(payload.approvedOvertimeMinutes !== undefined
      ? { approvedOvertimeMinutes: payload.approvedOvertimeMinutes }
      : {}),
    ...(payload.approvedPaidHours !== undefined
      ? { approvedPaidHours: payload.approvedPaidHours }
      : {}),
  };
}

/** 带薪公假确认工时：0 < h ≤ 24，最多 2 位小数（与后端 INVALID_PAID_HOURS 一致）。 */
export function parsePublicHolidayPayHours(input: string): number | null {
  const value = Number(input.trim());
  if (!input.trim() || !Number.isFinite(value) || value <= 0 || value > 24) return null;
  return Math.round(value * 100) / 100;
}

/** 用餐未休息加工时：批准按申请分钟全额加回，拒绝必须填写审核备注（与后端 REVIEW_REMARK_REQUIRED 一致）。 */
export function validateAttendanceMealBreakApproval(input: {
  action: OvertimeApprovalAction;
  remark?: string;
}): "remarkRequired" | null {
  return input.action === "reject" && !input.remark?.trim() ? "remarkRequired" : null;
}

export function validateAttendanceOvertimeApproval(input: {
  candidateMinutes: number;
  approvedMinutes: number;
  action: OvertimeApprovalAction;
  remark?: string;
}): OvertimeApprovalValidationError | null {
  if (
    !Number.isInteger(input.approvedMinutes)
    || input.approvedMinutes < 0
    || input.approvedMinutes > input.candidateMinutes
  ) {
    return "outOfRange";
  }
  if (input.approvedMinutes % 15 !== 0) return "invalidIncrement";
  if (
    (input.action === "reject" || input.approvedMinutes < input.candidateMinutes)
    && !input.remark?.trim()
  ) {
    return "remarkRequired";
  }
  return null;
}
