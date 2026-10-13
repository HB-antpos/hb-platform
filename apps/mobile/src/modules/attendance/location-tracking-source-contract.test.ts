import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// 源码契约测试：原生模块（expo-location / expo-task-manager）与 apiClient 无法在 Node 下加载，
// 这里用源码断言守住“接线”层面的约束（审核守卫、强制停止路径、挂接点、接口路径、采集参数）。

const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = join(directory, "..", "..", "..");
const read = (path: string) => readFileSync(join(mobileRoot, path), "utf8");

const tracking = read("src/modules/attendance/location-tracking.ts");
const control = read("src/modules/attendance/location-tracking-control.ts");
const batchApi = read("src/modules/attendance/location-sample-batch-api.ts");
const sync = read("src/modules/attendance/location-sample-sync.ts");
const rootLayout = read("app/_layout.tsx");
const recoveryHook = read("src/modules/attendance/use-attendance-location-recovery.ts");

function sliceBetween(source: string, start: string, end: string) {
  const startIndex = source.indexOf(start);
  assert.notEqual(startIndex, -1, `找不到 ${start}`);
  const endIndex = source.indexOf(end, startIndex + start.length);
  return source.slice(startIndex, endIndex === -1 ? undefined : endIndex);
}

test("批量上传接口：路径、X-Skip-Auth-Redirect 与单点接口一致，且直接使用 apiClient", () => {
  assert.match(batchApi, /\/react\/v1\/attendance/);
  assert.match(batchApi, /`\$\{ATTENDANCE_BASE\}\/location-samples\/batch`/);
  assert.match(batchApi, /"X-Skip-Auth-Redirect":\s*"1"/);
  assert.match(batchApi, /import \{ apiClient \} from "@\/shared\/api\/client"/);
  assert.match(batchApi, /normalizeLocationSampleBatchResult\(response\.data\)/);
});

test("后台任务回调首先检查 iOS 审核会话守卫，之后才读取上下文/入队/上传", () => {
  const callback = sliceBetween(tracking, "TaskManager.defineTask(", "export async function ensureAttendanceBackgroundLocationPermission");
  const guardIndex = callback.indexOf("isIosReviewSessionActive()");
  assert.notEqual(guardIndex, -1);
  // processLocationTaskData 内部才会读上下文、入队；flush 在其后触发
  for (const sideEffect of ["processLocationTaskData(", "flushPendingLocationSamples("]) {
    assert.ok(guardIndex < callback.indexOf(sideEffect), `${sideEffect} 必须在审核守卫之后`);
  }
});

test("上传、自愈、前台恢复都带 iOS 审核会话守卫", () => {
  assert.match(
    sliceBetween(tracking, "export async function ensureAttendanceLocationTrackingHealthy", "export async function recoverAttendanceLocationTracking"),
    /if \(isIosReviewSessionActive\(\)\) \{\s*return "skipped";/,
  );
  assert.match(
    sliceBetween(tracking, "export async function recoverAttendanceLocationTracking", "\n}\n"),
    /if \(isIosReviewSessionActive\(\)\) \{\s*return;/,
  );
  // flush 本身的守卫：运行时装配把 isIosReviewSessionActive 注入 flusher
  assert.match(sync, /isReviewSessionActive:\s*isIosReviewSessionActive/);
});

test("下班停止包装：强制停止与审核会话先走 control 原函数（清队列、不 flush），普通下班才限时 flush", () => {
  const stop = sliceBetween(tracking, "export async function stopAttendanceLocationTracking(", "export type AttendanceLocationHealth");
  assert.match(stop, /options\?\.force \|\| isIosReviewSessionActive\(\)/);
  assert.ok(
    stop.indexOf("stopAttendanceLocationTrackingAndClearQueue(options)")
      < stop.indexOf("flushPendingLocationSamplesWithin()"),
    "强制路径必须在 flush 之前返回",
  );
  assert.match(stop, /stopAttendanceLocationTrackingKeepingPendingSamples\(\)/);
  assert.match(stop, /if \(stopFailed\) \{\s*throw stopError;/, "停止失败仍需把错误交还调用方");
});

test("control：stopAttendanceLocationTracking 签名不变，强制路径清空队列，保留队列的变体单独导出", () => {
  assert.match(control, /export async function stopAttendanceLocationTracking\(options\?: \{ force\?: boolean \}\)/);
  assert.match(control, /options\?\.force/);
  assert.match(control, /export async function clearPendingLocationSamples\(\)/);
  assert.match(control, /export async function stopAttendanceLocationTrackingKeepingPendingSamples\(\)/);
  const forced = sliceBetween(control, "export async function stopAttendanceLocationTracking(", "\n}\n");
  assert.match(forced, /stopTrackingTask\(\{ clearPendingSamples: true \}\)/);
  const keeping = sliceBetween(control, "export async function stopAttendanceLocationTrackingKeepingPendingSamples", "\n}\n");
  assert.match(keeping, /stopTrackingTask\(\{ clearPendingSamples: false \}\)/);
  assert.match(keeping, /isIosReviewSessionActive\(\)/);
});

test("采集参数：Android 60 秒、iOS deferred 5 分钟（独立常量），其余选项不变", () => {
  assert.match(control, /ANDROID_LOCATION_TIME_INTERVAL_MS = 60 \* 1000/);
  assert.match(control, /IOS_DEFERRED_UPDATES_INTERVAL_MS = 5 \* 60 \* 1000/);
  const options = sliceBetween(control, "export function buildAttendanceLocationTaskOptions", "\n}\n");
  assert.match(options, /accuracy: Location\.Accuracy\.Balanced/);
  assert.match(options, /timeInterval: ANDROID_LOCATION_TIME_INTERVAL_MS/);
  assert.match(options, /distanceInterval: 0/);
  assert.match(options, /deferredUpdatesInterval: IOS_DEFERRED_UPDATES_INTERVAL_MS/);
  assert.match(options, /deferredUpdatesDistance: 0/);
  assert.match(options, /pausesUpdatesAutomatically: false/);
  assert.match(options, /showsBackgroundLocationIndicator: true/);
  assert.match(options, /foregroundService:/);
  // 启动与自愈重启共用同一份选项
  assert.equal(tracking.match(/buildAttendanceLocationTaskOptions\(\)/g)?.length, 2);
});

test("挂接点：根布局挂载前台恢复 hook（受 sideEffectsEnabled 控制），且仍保留任务模块的启动期 import", () => {
  assert.match(rootLayout, /import "@\/modules\/attendance\/location-tracking";/);
  assert.match(rootLayout, /useAttendanceLocationRecovery\(\{ enabled: sideEffectsEnabled \}\)/);
  assert.match(recoveryHook, /AppState\.addEventListener\("change"/);
  assert.match(recoveryHook, /state === "active"/);
  assert.match(recoveryHook, /recoverAttendanceLocationTracking/);
});

test("自愈重启不重置上一个保留点：重启路径不写上下文", () => {
  const healthy = sliceBetween(tracking, "export async function ensureAttendanceLocationTrackingHealthy", "export async function recoverAttendanceLocationTracking");
  assert.doesNotMatch(healthy, /writeActiveShiftContext\(/);
  assert.match(healthy, /isAttendanceLocationWindowExpired\(/);
  assert.match(healthy, /stopAttendanceLocationTrackingKeepingPendingSamples\(\)/);
});

test("不改动用户主检出里有未提交改动的文件：新代码不依赖 attendance/api.ts 与 types.ts", () => {
  for (const file of [
    "location-sample-batch.ts",
    "location-sample-batch-api.ts",
    "location-sample-capture.ts",
    "location-sample-filter.ts",
    "location-sample-flush.ts",
    "location-sample-owner.ts",
    "location-sample-queue.ts",
    "location-sample-sync.ts",
    "location-tracking.ts",
    "location-tracking-control.ts",
  ]) {
    const source = read(`src/modules/attendance/${file}`);
    assert.doesNotMatch(source, /@\/modules\/attendance\/api"/, `${file} 不应依赖 attendance/api.ts`);
    assert.doesNotMatch(source, /@\/modules\/attendance\/types"/, `${file} 不应依赖 attendance/types.ts`);
  }
});
