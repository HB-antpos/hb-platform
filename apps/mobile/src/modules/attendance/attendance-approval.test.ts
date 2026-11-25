import assert from "node:assert/strict";
import {
  buildAttendanceApprovalReviewRequest,
  canRejectAttendanceApproval,
  getAttendanceApprovalErrorKey,
  shouldRefreshAfterApprovalError,
  getSupplementalAttendanceApprovalDetail,
  isKnownAttendanceApprovalSourceType,
  parsePublicHolidayPayHours,
  validateAttendanceMealBreakApproval,
  validateAttendanceOvertimeApproval,
} from "./attendance-approval";

// 用餐未休息加工时：批准不需要备注，拒绝必须填备注（与后端 REVIEW_REMARK_REQUIRED 一致）。
assert.equal(isKnownAttendanceApprovalSourceType("MealBreak"), true);
assert.equal(validateAttendanceMealBreakApproval({ action: "approve" }), null);
assert.equal(validateAttendanceMealBreakApproval({ action: "reject", remark: "  " }), "remarkRequired");
assert.equal(validateAttendanceMealBreakApproval({ action: "reject", remark: "当天有轮休" }), null);

assert.equal(isKnownAttendanceApprovalSourceType("Punch"), true);
assert.equal(isKnownAttendanceApprovalSourceType("Leave"), true);
assert.equal(isKnownAttendanceApprovalSourceType("PunchAdjustment"), true);
assert.equal(isKnownAttendanceApprovalSourceType("Overtime"), true);
assert.equal(isKnownAttendanceApprovalSourceType("MissingClockOut"), true);
assert.equal(isKnownAttendanceApprovalSourceType("FutureApproval"), false);

assert.equal(getSupplementalAttendanceApprovalDetail({
  sourceType: "Punch",
  displayedTitle: "Punch exception",
  detail: "ClockIn · Late · Traffic delay",
}), "ClockIn · Late · Traffic delay");
assert.equal(getSupplementalAttendanceApprovalDetail({
  sourceType: "Leave",
  displayedTitle: "Leave request",
  detail: "2026-07-23 – 2026-07-25 · Family care",
}), "2026-07-23 – 2026-07-25 · Family care");
assert.equal(getSupplementalAttendanceApprovalDetail({
  sourceType: "Punch",
  displayedTitle: "Punch exception",
  detail: " Punch exception ",
}), undefined);
assert.equal(getSupplementalAttendanceApprovalDetail({
  sourceType: "Overtime",
  displayedTitle: "Overtime approval",
  detail: "backend system detail",
}), undefined);

assert.deepEqual(
  buildAttendanceApprovalReviewRequest({
    approvalGuid: "approval-1",
    remark: "  Reduced to verified time  ",
    approvedOvertimeMinutes: 30,
  }),
  {
    reviewRemark: "Reduced to verified time",
    approvedOvertimeMinutes: 30,
  },
);
assert.deepEqual(
  buildAttendanceApprovalReviewRequest({
    approvalGuid: "approval-2",
    remark: "   ",
  }),
  { reviewRemark: undefined },
);
assert.deepEqual(
  buildAttendanceApprovalReviewRequest({
    approvalGuid: "approval-3",
    remark: "Rejected",
    approvedOvertimeMinutes: 0,
  }),
  { reviewRemark: "Rejected", approvedOvertimeMinutes: 0 },
);

assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 60,
    action: "approve",
  }),
  null,
);
assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 45,
    action: "approve",
  }),
  "remarkRequired",
);
assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 30,
    action: "approve",
    remark: "Approved to the rostered limit",
  }),
  null,
);
assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 20,
    action: "approve",
    remark: "Not supported",
  }),
  "invalidIncrement",
);
assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 75,
    action: "approve",
    remark: "Invalid request",
  }),
  "outOfRange",
);
assert.equal(
  validateAttendanceOvertimeApproval({
    candidateMinutes: 60,
    approvedMinutes: 0,
    action: "reject",
  }),
  "remarkRequired",
);

// 带薪公假确认（卡 B）：批准体带 approvedPaidHours，工时 0 < h ≤ 24、最多 2 位小数。
assert.equal(isKnownAttendanceApprovalSourceType("PublicHolidayPay"), true);
assert.deepEqual(
  buildAttendanceApprovalReviewRequest({ approvalGuid: "a", remark: " ", approvedPaidHours: 7.6 }),
  { reviewRemark: undefined, approvedPaidHours: 7.6 },
);
assert.equal("approvedPaidHours" in buildAttendanceApprovalReviewRequest({ approvalGuid: "a" }), false);
assert.equal(parsePublicHolidayPayHours("7.6"), 7.6);
assert.equal(parsePublicHolidayPayHours("7.666"), 7.67);
assert.equal(parsePublicHolidayPayHours("24"), 24);
assert.equal(parsePublicHolidayPayHours("0"), null);
assert.equal(parsePublicHolidayPayHours("24.5"), null);
assert.equal(parsePublicHolidayPayHours("abc"), null);
assert.equal(parsePublicHolidayPayHours(" "), null);

// 缺勤（NoShow）：已知类型；不能驳回；两个错误码有本地文案，已自动取消的要刷新列表。
assert.equal(isKnownAttendanceApprovalSourceType("NoShow"), true);
assert.equal(canRejectAttendanceApproval("NoShow"), false);
assert.equal(canRejectAttendanceApproval("MissingClockOut"), true);
assert.equal(canRejectAttendanceApproval("PublicHolidayPay"), true);
assert.equal(getAttendanceApprovalErrorKey("NO_SHOW_RESOLVED"), "approvals.board.noShow.resolvedError");
assert.equal(getAttendanceApprovalErrorKey("NO_SHOW_REJECT_NOT_ALLOWED"), "approvals.board.noShow.rejectNotAllowed");
assert.equal(getAttendanceApprovalErrorKey("MISSING_CLOCK_OUT_RESOLVED"), undefined, "其他错误码沿用后端文案");
assert.equal(getAttendanceApprovalErrorKey(undefined), undefined);
assert.equal(shouldRefreshAfterApprovalError("NO_SHOW_RESOLVED"), true);
assert.equal(shouldRefreshAfterApprovalError("NO_SHOW_REJECT_NOT_ALLOWED"), false);
assert.equal(buildAttendanceApprovalReviewRequest({ approvalGuid: "ns", remark: "" }).reviewRemark, undefined, "确认缺勤备注可空");

console.log("attendance-approval.test.ts: ok");
