import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const sheet = readFileSync(join(directory, "ManagedPunchEditSheet.tsx"), "utf8");
const card = readFileSync(join(directory, "ManagedPunchRecordsCard.tsx"), "utf8");
const screen = readFileSync(join(directory, "AttendanceScreen.tsx"), "utf8");
const adjustmentCard = readFileSync(join(directory, "PunchAdjustmentCard.tsx"), "utf8");

assert.match(sheet, /runLatestAttendanceAdjustmentRequest/,
  "店长修改的 preview 与 submit 必须经过请求隔离器");
assert.match(sheet, /previewRevision:\s*preview\.previewRevision/,
  "店长保存必须原样回传当前 preview revision");
assert.match(sheet, /previewFingerprint === fingerprint/,
  "店长保存前必须确认表单与预览一致");
assert.match(sheet, /\$\{payload\.userGuid\}\|\$\{buildAttendancePunchAdjustmentFingerprint\(payload\)\}/,
  "预览指纹必须包含被修改员工，避免跨员工误用预览");
assert.match(sheet, /disabled=\{isBusy \|\| !canSubmit\}/,
  "缺少或过期 preview 时必须禁用保存");
assert.match(sheet, /const isEditable = canAdjust && isWithinWindow/,
  "无权限或超出工资周窗口时只能查看");
assert.match(sheet, /managedRecords\.auditNotice/,
  "店长直接生效前必须提示会留痕");
assert.match(card, /classifyManagedRecord\(session, today\)/,
  "列表必须按统一规则识别异常");
assert.match(card, /sortManagedRecords\(/,
  "列表必须异常优先排序");
assert.match(card, /queryKey: \["attendance", "approvals"\]/,
  "修改后必须刷新待审事项");
assert.match(screen, /canViewManagedPunches\s*\?\s*\[\{ value: "punches"/,
  "无 Punch.ViewManagedStore 权限时不得出现打卡记录页签");
assert.match(screen, /canAdjust=\{access\.canAdjustAttendancePunch\}/,
  "修改入口由补卡修改权限控制");
assert.match(screen, /isManagerStore=\{Boolean\(\s*access\.canAdjustAttendancePunch &&/,
  "店长本人补卡直接生效的提示必须与后端一致：管理该店且持有补卡修改权限");
assert.match(adjustmentCard, /<BusinessSheet/,
  "员工补卡表单收进底部弹层，不再常驻页面");

console.log("managed-punch-records-contract.test.ts: ok");
