import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "api.ts"), "utf8");
const types = readFileSync(join(directory, "types.ts"), "utf8");

assert.match(source, /export async function getMyAttendancePunchAdjustments/);
assert.match(source, /apiClient\.get\(`\$\{ATTENDANCE_BASE\}\/my\/punch-adjustments`\)/);
assert.match(source, /export async function previewMyAttendancePunchAdjustment/);
assert.match(source, /apiClient\.post\(\s*`\$\{ATTENDANCE_BASE\}\/my\/punch-adjustments\/preview`/);
assert.match(source, /normalizeAttendancePunchAdjustmentPreview\(response\.data\)/);
assert.match(source, /export async function createMyAttendancePunchAdjustment/);
assert.match(source, /apiClient\.post\(\s*`\$\{ATTENDANCE_BASE\}\/my\/punch-adjustments`/);
assert.match(
  types,
  /requestedPunchTimeUtc\??:\s*string/,
  "preview/create 的共享 payload 必须携带 requestedPunchTimeUtc",
);
assert.match(
  source,
  /sanitizePayload\(\{ \.\.\.payload, reason: payload\.reason\.trim\(\) \}\)/,
  "preview/create 必须原样发送 payload 内的 requestedPunchTimeUtc",
);

// 店长代员工补卡：独立的 managed 接口，payload 必须带被修改员工。
assert.match(source, /export async function previewManagedAttendancePunchAdjustment/);
assert.match(source, /apiClient\.post\(\s*`\$\{ATTENDANCE_BASE\}\/managed\/punch-adjustments\/preview`/);
assert.match(source, /export async function createManagedAttendancePunchAdjustment/);
assert.match(source, /apiClient\.post\(\s*`\$\{ATTENDANCE_BASE\}\/managed\/punch-adjustments`/);
assert.match(
  types,
  /interface AttendanceManagedPunchAdjustmentPayload extends AttendancePunchAdjustmentPayload \{\s*userGuid: string;/,
  "managed payload 必须在本人补卡字段基础上携带 userGuid",
);
assert.match(
  source,
  /apiClient\.get\(`\$\{ATTENDANCE_BASE\}\/records`[\s\S]{0,200}fromDate: params\.workDate,\s*toDate: params\.workDate/,
  "店长打卡记录按单日查询",
);

console.log("attendance-punch-adjustment-api.test.ts: ok");
