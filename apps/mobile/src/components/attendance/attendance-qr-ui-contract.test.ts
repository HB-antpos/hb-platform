import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const source = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), "AttendanceScreen.tsx"),
  "utf8",
);
const todayCardSource = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), "TodayPunchCard.tsx"),
  "utf8",
);
const scanSoundSource = readFileSync(
  join(
    dirname(fileURLToPath(import.meta.url)),
    "../../modules/scanner/scan-sound.ts",
  ),
  "utf8",
);

assert.match(source, /attendanceScannerError/,
  "扫描器必须维护 Modal 内可见错误状态");
assert.match(source, /setAttendanceScannerError\(/,
  "本地格式、门店或权限错误必须写入扫描器错误状态");
assert.match(source, /attendanceScannerPaused/,
  "任一失败后必须暂停相机扫码事件");
assert.match(source, /retryAttendanceScan/,
  "Modal 必须提供显式重新扫描动作并重置 gate");
assert.match(source, /scanner\.retry/,
  "重新扫描按钮必须使用中英文 i18n 文案");
assert.match(source, /selectable[^>]*>\s*\{attendanceScannerError\}/,
  "Modal 内错误必须可见且可复制，用户才能修正后重扫或取消");
assert.match(todayCardSource, /canOpenAttendanceQrScanner/,
  "扫码入口必须使用与当前门店打卡完成状态无关的启用规则");
assert.doesNotMatch(todayCardSource, /\bcanPunch\b/,
  "扫码入口不得被当前门店 canPunch 或 DAY_COMPLETE 禁用");
assert.doesNotMatch(todayCardSource, /today\.info\.location|openLocationInSystemMap|actions\.viewLocation/,
  "前台 Today 卡不得显示定位详情或地图入口，定位只用于后台采集和打卡提交");

const validateIndex = source.indexOf("validateAttendanceQrToken(normalizedQrToken)");
const hideScannerIndex = source.indexOf("hideAttendanceScannerForProcessing();", validateIndex);
const resolveIndex = source.indexOf("resolveAttendanceQr(normalizedQrToken)");
const preparationIndex = source.indexOf("prepareAttendanceQrPunch(qrToday.nextPunchType");
// 普通打卡与二维码打卡共存时，只验证二维码准备完成后的 punch 调用顺序。
const punchIndex = source.indexOf("punchMutation.mutateAsync(", preparationIndex);
const successSoundIndex = source.indexOf("playAttendancePunchSuccessSound();", punchIndex);
const trackingIndex = source.indexOf("applyAttendanceTrackingLifecycle(result");
const uiGateAfterTrackingIndex = source.indexOf(
  "if (!attendanceScannerSessionGate.isActive(session)) return;",
  trackingIndex,
);
assert.ok(validateIndex >= 0, "扫码后必须先做 opaque token 格式预检");
assert.ok(hideScannerIndex > validateIndex, "格式预检通过后必须立即卸载相机");
assert.ok(resolveIndex > hideScannerIndex, "卸载相机后立即调用后端 resolve");
assert.doesNotMatch(source, /verifyAttendanceNetworkReachability/,
  "二维码校验前不得等待独立 health 探测而消耗有效期");
assert.ok(preparationIndex > resolveIndex, "后端 resolve 门店后才准备权限与实时 GPS");
assert.ok(punchIndex > preparationIndex, "权限与最新 GPS 完成后才允许 punch");
assert.ok(successSoundIndex > punchIndex, "打卡成功返回后必须播放专用成功音");
assert.ok(trackingIndex > successSoundIndex, "成功音必须位于 punch 成功与 tracking 之间");
assert.ok(source.indexOf("setLastQrPunch(result)") < trackingIndex,
  "已保存结果必须在后台定位生命周期完成前展示");
assert.doesNotMatch(source.slice(source.indexOf("const punchMutation"), source.indexOf("const previewPunchAdjustmentMutation")),
  /invalidateEmployeeData|onSuccess/, "提交结果不得继续等待查询刷新");
assert.ok(uiGateAfterTrackingIndex > trackingIndex,
  "tracking 生命周期必须位于服务端成功后的 UI session gate 之前");
assert.doesNotMatch(source, /parseAttendanceQrMetadata/,
  "AttendanceScreen 不得从二维码 payload 推断门店或设备");
assert.match(source, /const resetAttendanceScannerUi = \(\) => \{[\s\S]{0,300}setAttendanceScannerSubmitting\(false\)/,
  "关闭或显式重试必须清理 resolve/punch 提交态");
assert.match(
  source,
  /const hideAttendanceScannerForProcessing = \(\) => \{[\s\S]{0,160}setAttendanceScannerVisible\(false\)[\s\S]{0,160}\};/,
  "扫到有效格式后只隐藏 Modal，不得 invalidate 当前 session",
);
assert.match(
  source,
  /const failAttendanceQrProcessing = \([\s\S]{0,500}showMessage\(message\)[\s\S]{0,500}finishSubmitting\(session\)[\s\S]{0,500}setAttendanceScannerSubmitting\(false\)/,
  "Modal 隐藏后失败必须走页面 Snackbar 并释放提交态",
);
assert.match(
  source,
  /isPunching=\{attendanceScannerSubmitting \|\| punchMutation\.isPending \|\| isPreparingScan\}/,
  "TodayPunchCard 必须在扫码前的用餐核对、resolve、定位、punch 和 tracking 全阶段禁用扫码入口",
);
assert.match(
  source,
  /const openAttendanceScanner = async \(\) => \{[\s\S]{0,400}resolveClockOutMealDeclaration\(\)[\s\S]{0,300}beginAttendanceScannerSession\(\)/,
  "用餐确认必须在打开扫码器之前完成：二维码与打卡授权有有效期，扫完再问会过期",
);
assert.match(
  source,
  /buildAttendanceQrPunchPayload\([\s\S]{0,320}pendingMealDeclarationRef\.current/,
  "下班打卡必须带上扫码前的用餐回答",
);
assert.match(
  source,
  /const closeAttendanceScanner = [\s\S]{0,400}pendingMealDeclarationRef\.current = undefined/,
  "关闭扫码器即放弃本次下班，必须清掉用餐回答",
);
assert.match(
  source,
  /buildAttendanceQrPunchPayload\([\s\S]{0,220}resolvedQr\.punchAuthorizationToken/,
  "punch payload 必须透传 resolve 返回的短时凭证",
);
assert.match(source, /const attendanceCameraScan = useCameraScan\(\{[\s\S]{0,300}singleScanUntilReset:\s*true/,
  "考勤相机每个 resetKey 会话只允许转发一次二维码");
assert.match(scanSoundSource, /export function playAttendancePunchSuccessSound\(\)/,
  "扫码打卡必须使用独立成功音入口");
assert.equal(
  source.match(/playAttendancePunchSuccessSound\(\);/g)?.length,
  1,
  "一次打卡流程只能触发一次成功音",
);

console.log("attendance-qr-ui-contract.test.ts: ok");
