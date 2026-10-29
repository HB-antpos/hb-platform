import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (name: string) => readFileSync(join(directory, name), "utf8");
const list = read("ManagerApprovalList.tsx");
const card = read("approvals/ApprovalCard.tsx");
const overtime = read("approvals/OvertimeReviewDetail.tsx");
const missing = read("approvals/MissingClockOutDetail.tsx");
const sick = read("approvals/SickLeaveReviewDetail.tsx");

// 列表：按工作日分组、本人申请判定、只认已知类型做筛选、显示打开时对账新增条数。
assert.match(list, /groupApprovalsByWorkDate/);
assert.match(list, /isSelfApproval\(item, currentUserGuid\)/);
assert.match(list, /isKnownAttendanceApprovalSourceType/);
assert.match(list, /approvals\.board\.reconcile/);
assert.doesNotMatch(card, /<Text[^>]*>\{item\.title\}<\/Text>/);

// 卡片：补卡写「从什么改成什么」；本人申请默认锁定，有 ReviewSelf 权限才给按钮且须二次确认。
assert.match(card, /originalPunchTimeLocal/);
assert.match(card, /requestedPunchTimeLocal/);
assert.match(card, /approvals\.board\.selfReview\.locked/);
assert.match(card, /Alert\.alert\(/);
// 用餐未休息加工时：拒绝要备注、批准按钮写明「批准加回」、细节行展示排班次数与记录次数。
assert.match(card, /validateAttendanceMealBreakApproval/);
assert.match(card, /actions\.approveMealBreak/);
assert.match(card, /approvals\.mealBreakDetail/);

// 加班详情：分钟只能选 15 的倍数，少批 / 拒绝沿用原校验（必须写备注）。
assert.match(overtime, /listOvertimeMinuteOptions\(candidate\)/);
assert.match(overtime, /validateAttendanceOvertimeApproval/);
assert.match(overtime, /approvedOvertimeMinutes: approvedMinutes/);

// 漏下班：步进器填时间、服务端预览计薪，保存前用真实原因重新预览拿版本号。
assert.match(missing, /PunchTimeStepper/);
assert.match(missing, /previewManagedAttendancePunchAdjustment\(payload\)/);
assert.match(missing, /previewRevision: fresh\.previewRevision/);

// 病假：医嘱可点开大图；拒绝必须写原因。
assert.match(sick, /maximumZoomScale/);
assert.match(sick, /approvals\.board\.sick\.rejectReasonRequired/);

console.log("manager-approval-list-contract.test.ts: ok");
