import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "ManagerApprovalList.tsx"), "utf8");

assert.match(source, /validateAttendanceOvertimeApproval/);
assert.match(source, /candidateOvertimeMinutes/);
assert.match(source, /approvedOvertimeMinutes/);
assert.match(source, /keyboardType="number-pad"/);
assert.match(source, /actions\.approveOvertime/);
assert.match(source, /actions\.rejectOvertime/);
assert.match(source, /adjustmentOriginal/);
assert.match(source, /adjustmentRequested/);
assert.match(source, /isKnownAttendanceApprovalSourceType/);
assert.match(source, /getSupplementalAttendanceApprovalDetail/);
assert.match(source, /supplementalDetail \?/);
assert.match(source, /approvals\.presentation/);
assert.match(source, /adjustment\.effectivePunchTimeLocal \?/);
assert.doesNotMatch(source, /<Text[^>]*>\{item\.title\}<\/Text>/);
// 用餐未休息加工时：拒绝要备注、批准按钮写明「批准加回」、细节行展示排班次数与记录次数。
assert.match(source, /validateAttendanceMealBreakApproval/);
assert.match(source, /actions\.approveMealBreak/);
assert.match(source, /approvals\.mealBreakDetail/);

console.log("manager-approval-list-contract.test.ts: ok");
