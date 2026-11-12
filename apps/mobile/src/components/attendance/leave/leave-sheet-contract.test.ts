import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(join(directory, relative), "utf8");
const panel = read("../MyRequestsPanel.tsx");
const sheet = read("OtherLeaveSheet.tsx");
const picker = read("LeavePickerOptions.tsx");
const home = read("LeaveHomeScreen.tsx");

// —— 临时工：点「请假」直接打开无薪表单，不再先弹「请哪种假」——
assert.match(panel, /const casual = shouldOpenTimeOffDirectly\(options\)/, "按后端 paidLeaveEligible 判断临时工");
assert.match(panel, /const openLeave = \(\) => \(casual \? setOtherVisible\(true\) : setPickerVisible\(true\)\)/, "临时工跳过选择层");
assert.match(panel, /enabled: canApplyLeave,\n/, "请假参数进页面就读，点入口时才知道要不要跳过");
assert.match(panel, /<LeavePickerOptions/, "全职 / 兼职用新的选择层内容");
assert.match(panel, /options=\{options\}\n/, "无薪表单拿到规则参数（排班点、临时工文案）");
assert.match(home, /options=\{options\}\n/, "「申请假期」首页的无薪表单同样传入参数");

// —— 无薪表单：两种原因提交后端已有类型，日历区间，生病才出现医嘱提示 ——
assert.match(sheet, /leaveType: unpaidLeaveTypeOf\(reason\)/, "原因映射到 SickLeave / AnnualLeave，不改接口");
assert.match(sheet, /tapLeaveRange\(current, date\)/, "日历点选区间");
assert.match(sheet, /effectiveLeaveRange\(selection\)/, "只点了起点按单日提交");
assert.match(sheet, /reason === "sick" \?/, "只有选生病才出现医嘱提示");
assert.match(sheet, /casual \? "timeOff\.titleCasual" : "timeOff\.titleOther"/, "临时工与正式工标题不同");
assert.doesNotMatch(sheet, /SegmentedButtons/, "不再用「Annual leave / Sick leave」分段按钮");

// —— 选择层：日期来自规则换算，不读设备时间 ——
assert.match(picker, /buildLeavePickerSummary\(paid\)/, "可请日期由规则与门店今天换算");
assert.doesNotMatch(picker, /new Date\(/, "选择层不读设备时间");

console.log("leave-sheet-contract.test.ts: ok");
