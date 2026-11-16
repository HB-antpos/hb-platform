import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(join(directory, relative), "utf8");
const screen = read("AttendanceScreen.tsx");
const card = read("TodayPunchCard.tsx");
const notice = read("LocationUsageNotice.tsx");
const sheet = read("LocationUsageSheet.tsx");
const zh = JSON.parse(read("../../locales/zh/screens/attendance.json"));
const en = JSON.parse(read("../../locales/en/screens/attendance.json"));

// ── 上班扫码前的披露：用底部弹层，不再是只有「取消 / 继续」的系统 Alert ──
assert.doesNotMatch(
  screen.slice(screen.indexOf("const confirmBackgroundLocationUsage"), screen.indexOf("const settleLocationConsent")),
  /Alert\.alert/,
  "上班定位披露必须用弹层承载三段式说明，不能退回只有一句话的系统 Alert",
);
assert.match(screen, /<LocationUsageSheet[\s\S]{0,120}mode="consent"/,
  "AttendanceScreen 必须渲染 consent 模式的定位说明弹层");
assert.match(
  screen,
  /const settleLocationConsent = useCallback\(\(accepted: boolean\) => \{[\s\S]{0,200}locationConsentResolverRef\.current = null[\s\S]{0,120}resolve\?\.\(accepted\)/,
  "弹层结算必须先清掉 resolver 再结算，保证每个 Promise 只结算一次",
);
assert.match(
  screen,
  /useEffect\(\(\) => \(\) => \{\s*locationConsentResolverRef\.current\?\.\(false\)/,
  "页面卸载时未结算的弹层必须按取消结算，扫码流程不能永远挂起",
);

// 披露放在打开扫码器之前（二维码与打卡授权有有效期），且取消即不打开扫码器。
assert.match(
  screen,
  /const openAttendanceScanner = async \(\) => \{[\s\S]{0,900}confirmLocationUsageBeforeClockIn\(\)[\s\S]{0,400}beginAttendanceScannerSession\(\)/,
  "定位披露必须在 beginAttendanceScannerSession 之前完成",
);
assert.match(
  screen,
  /const confirmLocationUsageBeforeClockIn = async[\s\S]{0,700}resolveAttendanceTodayStatus\(todayQuery\.data, true\) !== "readyToClockIn"[\s\S]{0,200}hasAttendanceBackgroundLocationPermission\(\)/,
  "只在当天、待上班、后台定位尚未授权时才披露；已授权的员工不重复打扰",
);
assert.match(
  screen,
  /locationUsageConsentedRef\.current \|\| await confirmBackgroundLocationUsage\(\)/,
  "扫码前已同意过，扫码后申请系统权限时不得重复弹说明",
);

// 定位生命周期跑完就通知打卡页重新读取真实状态，且不受 UI 会话失效影响。
assert.match(
  screen,
  /setTrackingStateVersion\(\(version\) => version \+ 1\);\s*if \(!attendanceScannerSessionGate\.isActive\(session\)\) return;/,
  "定位启动 / 停止完成后必须无条件刷新状态，位置在 UI 会话 gate 之前",
);
assert.match(screen, /locationTrackingState=\{locationTrackingState\}/,
  "必须把班中定位真实状态传给今日卡");

// ── 今日卡：状态由纯函数决定，只展示真实状态 ──
assert.match(card, /resolveLocationNoticeVariant\(\{/, "今日卡必须用统一的纯函数决定展示哪种说明");
assert.match(
  card,
  /segment\.clockOut && !segment\.clockOut\.isSystemAutoClockOut/,
  "系统补的午夜自动下班卡不是真实下班，不能据此显示「定位已停止」",
);
assert.match(card, /tracking: locationTrackingState \?\? "unknown"/,
  "没有读到真实状态时按 unknown 处理，不显示开启 / 停止状态条");
// 既有契约：今日卡不得出现定位详情 / 地图入口；本次只是文字说明，不能误触该约束。
assert.doesNotMatch(card + notice + sheet, /today\.info\.location|openLocationInSystemMap|actions\.viewLocation/,
  "定位使用说明不得引入定位详情或地图入口");
assert.match(sheet, /BusinessSheet/, "弹层统一使用 BusinessSheet，保持和其他业务弹层一致");

// ── 文案：中英文都不得出现「每隔几分钟记录」类频率表述，并要写清下班后停止 ──
const frequencyWording = /每隔|定期|每\s*\d+\s*(分钟|秒)|几分钟|分钟记录|every (few )?(minutes?|seconds?)|periodic|per minute|\d+\s*minutes? interval/i;
const flatten = (value: unknown): string[] =>
  typeof value === "string" ? [value] : Object.values(value as object).flatMap(flatten);
for (const [name, copy] of [["zh", zh.locationNotice], ["en", en.locationNotice]] as const) {
  for (const text of flatten(copy)) {
    assert.doesNotMatch(text, frequencyWording, `${name} 定位说明文案不得出现采集频率表述: ${text}`);
  }
}
assert.match(zh.locationNotice.afterBody, /下班/, "中文说明必须写清下班打卡后停止");
assert.match(zh.locationNotice.afterBody, /不再使用/, "中文说明必须写清下班后不再使用位置");
assert.match(en.locationNotice.afterBody, /clock out/i, "英文说明必须写清下班打卡后停止");
// 旧的 backgroundLocation 文案已被取代，不留死键。
assert.equal("backgroundLocation" in zh, false, "旧 backgroundLocation 文案应已删除");
assert.equal("backgroundLocation" in en, false, "旧 backgroundLocation 文案应已删除");

console.log("location-usage-notice-contract.test.ts: ok");
