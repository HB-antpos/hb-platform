import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// 班中后台定位已取消：App 只在扫码上班 / 下班时读取位置。
// 本契约守住两件事：打卡页不再有任何后台定位相关的披露 / 申请 / 状态条；说明文案如实、不夸大也不留旧口径。

const directory = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(join(directory, relative), "utf8");
const screen = read("AttendanceScreen.tsx");
const card = read("TodayPunchCard.tsx");
const notice = read("LocationUsageNotice.tsx");
const sheet = read("LocationUsageSheet.tsx");
const zh = JSON.parse(read("../../locales/zh/screens/attendance.json"));
const en = JSON.parse(read("../../locales/en/screens/attendance.json"));

// ── 打卡页不再申请后台权限、不再启动后台任务、不再有扫码前的「同意并继续」披露 ──
assert.doesNotMatch(
  screen,
  /confirmBackgroundLocationUsage|ensureQrPunchBackgroundLocationPermission|ensureAttendanceBackgroundLocationPermission|hasAttendanceBackgroundLocationPermission|startAttendanceLocationTracking|<LocationUsageSheet|locationUsageConsentedRef|useShiftLocationTrackingState/,
  "AttendanceScreen 不得再有后台定位的申请 / 披露 / 启动 / 状态读取",
);
assert.match(
  screen,
  /prepareAttendanceQrPunch\(\{\s*isActive:[\s\S]{0,200}refreshVerification:/,
  "扫码准备阶段只刷新最新 GPS 与网络，不再有后台权限这一关",
);
assert.match(
  screen,
  /applyAttendanceTrackingLifecycle\(result, \{\s*stop:/,
  "保存后只剩下班兜底停止旧版后台定位，且不再有 start",
);
assert.doesNotMatch(screen, /messages\.(locationTrackingFailed|qrTrackingWarning|backgroundLocationRequiredForClockIn)/,
  "不得再向员工提示「定位任务启动失败 / 请允许始终定位」");
// 定位生命周期（停止旧任务）出错不影响已保存的打卡，也不弹提示。
assert.match(
  screen,
  /\.catch\(\(error\) => \{\s*console\.warn\("\[attendance\] 下班停止旧版班中定位失败", error\);/,
  "下班停止旧版定位失败只记日志，不能阻断打卡成功反馈",
);

// ── 今日卡：说明卡只在当天上班前显示，不再有「已开启 / 已停止」状态条 ──
assert.match(card, /const showLocationNotice = allowPunch && heroVariant === "before"/,
  "说明卡只在当天、上班前显示");
assert.doesNotMatch(card, /locationTrackingState|resolveLocationNoticeVariant|trackingWarning/,
  "今日卡不得再接收班中定位状态 / 警告");
assert.doesNotMatch(notice, /LocationNoticeVariant|activeTitle|stoppedTitle/, "说明卡只剩一种形态");
// 既有契约：今日卡不得出现定位详情 / 地图入口；本次只是文字说明，不能误触该约束。
assert.doesNotMatch(card + notice + sheet, /today\.info\.location|openLocationInSystemMap|actions\.viewLocation/,
  "定位使用说明不得引入定位详情或地图入口");
assert.match(sheet, /BusinessSheet/, "弹层统一使用 BusinessSheet，保持和其他业务弹层一致");
assert.doesNotMatch(sheet, /mode: "consent"|onConfirm|consent/i, "弹层只读，不再有「同意并继续」模式");

// ── 文案：不得出现采集频率表述，不得再说「持续 / 后台使用位置」，要写清只在扫码时使用 ──
const frequencyWording = /每隔|定期|每\s*\d+\s*(分钟|秒)|几分钟|分钟记录|every (few )?(minutes?|seconds?)|periodic|per minute|\d+\s*minutes? interval/i;
const flatten = (value: unknown): string[] =>
  typeof value === "string" ? [value] : Object.values(value as object).flatMap(flatten);
for (const [name, copy] of [["zh", zh.locationNotice], ["en", en.locationNotice]] as const) {
  for (const text of flatten(copy)) {
    assert.doesNotMatch(text, frequencyWording, `${name} 定位说明文案不得出现采集频率表述: ${text}`);
  }
}
// 旧口径（班中持续使用位置、下班后停止、补传）已不成立，文案里不能残留。
const staleKeys = [
  "shiftLabel", "shiftBody", "afterLabel", "afterBody", "activeTitle", "activeBody",
  "explain", "stoppedTitle", "stoppedBody",
];
const staleSheetKeys = [
  "consentTitle", "consentLead", "consentTip", "consentConfirm", "shiftTitle", "shiftBody",
  "afterTitle", "afterBody", "autoStopTitle", "autoStopBody", "uploadTitle", "uploadBody",
];
for (const [name, copy] of [["zh", zh.locationNotice], ["en", en.locationNotice]] as const) {
  for (const key of staleKeys) assert.equal(key in copy, false, `${name} locationNotice.${key} 是旧口径，应已删除`);
  for (const key of staleSheetKeys) assert.equal(key in copy.sheet, false, `${name} locationNotice.sheet.${key} 是旧口径，应已删除`);
}
for (const key of ["qrTrackingWarning", "backgroundLocationRequiredForClockIn", "locationTrackingFailed"]) {
  assert.equal(key in zh.messages, false, `zh messages.${key} 应已删除`);
  assert.equal(key in en.messages, false, `en messages.${key} 应已删除`);
}
assert.match(zh.locationNotice.scanBody, /扫码/, "中文说明必须写清只在扫码打卡时使用位置");
assert.match(zh.locationNotice.otherBody, /不会在后台/, "中文说明必须写清不会在后台使用位置");
assert.match(en.locationNotice.scanBody, /scan/i, "英文说明必须写清只在扫码打卡时使用位置");
assert.match(en.locationNotice.otherBody, /not use your location in the background/i, "英文说明必须写清不会在后台使用位置");
// 权限提示不得再要求「始终允许」。
assert.doesNotMatch(zh.locationPermission.description, /后台/, "权限提示不得再要求后台定位");
assert.doesNotMatch(en.locationPermission.description, /background/i, "权限提示不得再要求后台定位");

console.log("location-usage-notice-contract.test.ts: ok");
