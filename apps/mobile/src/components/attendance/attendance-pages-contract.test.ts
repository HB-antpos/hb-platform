import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (name: string) => readFileSync(join(directory, name), "utf8");
const screen = read("AttendanceScreen.tsx");
const availabilitySheet = read("AvailabilitySheet.tsx");
const requests = read("MyRequestsPanel.tsx");
const schedule = read("MySchedulePanel.tsx");

assert.match(screen, /getAttendanceEmployees\(selectedStoreCode!\)/,
  "排班与登记请假的员工列表必须走考勤接口，不能要求 Users.View");
assert.doesNotMatch(screen, /useStoreUsers\(/,
  "考勤页不得再调用用户管理的店员列表");
assert.match(screen, /enabled: Boolean\(isAuthenticated && user && isManagementTab\)/,
  "待审数量角标需要在管理页任一页签拉取待审事项");
assert.match(screen, /canAdjustPunch=\{access\.canAdjustAttendancePunch\}/,
  "漏下班在审核详情里直接补下班卡，必须按修改打卡权限放行");
assert.match(screen, /canReviewSelf=\{access\.canReviewOwnAttendance\}/,
  "本人审核只认 Attendance.Approval.ReviewSelf");
assert.match(screen, /value: "requests"/, "员工端必须提供「申请」页签");

assert.match(availabilitySheet, /AvailabilityBatchSaveError/,
  "可上班时间保存失败必须保留未保存日期");
assert.match(availabilitySheet, /await onVerify\(verification\.payload\)/,
  "结果未确认时只能只读核对，不得直接重发新增");
assert.doesNotMatch(availabilitySheet, /AvailabilityTimePicker|AvailabilityDatePicker/,
  "弹层内不得再嵌套二级原生 Modal 选择器");

assert.match(requests, /reviewRemark/, "驳回原因必须展示给员工");
assert.match(requests, /reviewedByName/, "审核人必须展示给员工");
assert.match(schedule, /buildMyWeekRows\(weekStartDate, today, week, availability\)/,
  "排班页必须把班次与可上班时间按日期合并展示");

console.log("attendance-pages-contract.test.ts: ok");
