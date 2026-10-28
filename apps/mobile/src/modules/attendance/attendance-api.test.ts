import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "api.ts"), "utf8");
const types = readFileSync(join(directory, "types.ts"), "utf8");

assert.match(types, /interface AttendanceQrResolveResult\s*\{[\s\S]*storeCode:\s*string;[\s\S]*deviceCode:\s*string;[\s\S]*expiresAtUtc:\s*string;[\s\S]*punchAuthorizationToken\?:\s*string;[\s\S]*punchAuthorizationExpiresAtUtc\?:\s*string;[\s\S]*storeName\?:\s*string;/,
  "resolve 结果必须暴露后端验证身份，并兼容旧后端不返回短时凭证");
assert.match(types, /interface AttendancePunchPayload\s*\{[\s\S]*qrToken:\s*string;[\s\S]*punchAuthorizationToken\?:\s*string;/,
  "punch payload 必须支持向后端透传可选短时凭证");
assert.match(source, /export async function resolveAttendanceQr\(\s*qrToken:\s*string[\s\S]*apiClient\.post\(\s*`\$\{ATTENDANCE_BASE\}\/qr\/resolve`\s*,\s*\{\s*qrToken\s*\}/,
  "resolve 必须通过现有 apiClient POST 原始 token");
assert.match(source, /import \{[\s\S]{0,200}normalizeAttendanceQrResolveResult[\s\S]{0,200}\} from "@\/modules\/attendance\/attendance-qr";/,
  "resolve 必须复用已执行测试的 fail-closed normalizer");
assert.match(source, /return normalizeAttendanceQrResolveResult\(response\.data\)/,
  "resolve 请求必须通过 fail-closed normalizer 后才能返回页面");
assert.doesNotMatch(source, /resolveAttendanceQr[\s\S]{0,500}(useQuery|queryClient|retry)/,
  "30 秒 resolve 结果不得缓存或业务重试");
assert.match(source, /return normalizeAttendancePunchMutationResult\(\s*response\.data,\s*normalizePunch\(/,
  "punch mutation 成功响应必须经过严格 normalizer，不能直接返回历史宽松结果");

console.log("attendance-api.test.ts: ok");

// 排班「点选目标格」用到的两个接口：恢复已删除排班、本周打卡的排班 GUID（判断哪些班次已有打卡）
assert.match(source, /export async function restoreAttendanceSchedule\(scheduleGuid: string\)[\s\S]{0,200}apiClient\.post\(`\$\{ATTENDANCE_BASE\}\/schedules\/\$\{encodeURIComponent\(scheduleGuid\)\}\/restore`\)/,
  "恢复排班应 POST /schedules/{guid}/restore");
assert.match(source, /export async function getAttendancePunchScheduleGuids[\s\S]{0,400}apiClient\.get\(`\$\{ATTENDANCE_BASE\}\/punches`[\s\S]{0,200}fromDate: params\.fromDate/,
  "本周打卡应 GET /punches 并按起止日期过滤");
console.log("attendance-api.test.ts (schedule placement): ok");
