import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// 源码契约测试：原生模块（expo-location / expo-task-manager）与 apiClient 无法在 Node 下加载，
// 这里用源码断言守住“接线”层面的约束。
// 班中后台定位已取消：本契约的核心是「不会再有任何代码启动后台定位 / 申请后台权限」，
// 以及「旧版本留下的后台任务一定会被收掉」。

const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = join(directory, "..", "..", "..");
const read = (path: string) => readFileSync(join(mobileRoot, path), "utf8");

const tracking = read("src/modules/attendance/location-tracking.ts");
const control = read("src/modules/attendance/location-tracking-control.ts");
const batchApi = read("src/modules/attendance/location-sample-batch-api.ts");
const sync = read("src/modules/attendance/location-sample-sync.ts");
const rootLayout = read("app/_layout.tsx");
const cleanupHook = read("src/modules/attendance/use-legacy-location-cleanup.ts");

function sliceBetween(source: string, start: string, end: string) {
  const startIndex = source.indexOf(start);
  assert.notEqual(startIndex, -1, `找不到 ${start}`);
  const endIndex = source.indexOf(end, startIndex + start.length);
  return source.slice(startIndex, endIndex === -1 ? undefined : endIndex);
}

/** 递归收集目录下的非测试 ts / tsx 源码（相对 mobileRoot 的路径）。 */
function collectProductionSources(relativeDir: string): string[] {
  const absolute = join(mobileRoot, relativeDir);
  return readdirSync(absolute, { withFileTypes: true }).flatMap((entry) => {
    const relative = join(relativeDir, entry.name);
    if (entry.isDirectory()) {
      return entry.name === "node_modules" ? [] : collectProductionSources(relative);
    }
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.(ts|tsx)$/.test(entry.name) ? [relative] : [];
  });
}

test("全应用生产源码里没有任何启动后台定位 / 申请后台权限的调用", () => {
  const offenders = [...collectProductionSources("src"), ...collectProductionSources("app")]
    .filter((file) => /\b(startLocationUpdatesAsync|requestBackgroundPermissionsAsync|startGeofencingAsync)\b/.test(read(file)));
  assert.deepEqual(offenders, [], "班中后台定位已取消，不得再启动后台定位任务或申请后台权限");
});

test("批量上传接口：路径、X-Skip-Auth-Redirect 与单点接口一致，且直接使用 apiClient", () => {
  assert.match(batchApi, /\/react\/v1\/attendance/);
  assert.match(batchApi, /`\$\{ATTENDANCE_BASE\}\/location-samples\/batch`/);
  assert.match(batchApi, /"X-Skip-Auth-Redirect":\s*"1"/);
  assert.match(batchApi, /import \{ apiClient \} from "@\/shared\/api\/client"/);
  assert.match(batchApi, /normalizeLocationSampleBatchResult\(response\.data\)/);
});

test("旧任务回调不再采集：先过审核会话守卫，然后只调用清理函数", () => {
  const callback = sliceBetween(tracking, "TaskManager.defineTask(", "\n}\n");
  const guardIndex = callback.indexOf("isIosReviewSessionActive()");
  assert.notEqual(guardIndex, -1);
  assert.ok(guardIndex < callback.indexOf("retireLegacyAttendanceLocationTracking()"), "清理必须在审核守卫之后");
  // 回调里不得再读上下文、过滤、入队。
  assert.doesNotMatch(callback, /captureShiftLocationFixes|filterShiftLocationFixes|locationSampleQueue\.enqueue|processLocationTaskData/);
  assert.doesNotMatch(tracking, /captureShiftLocationFixes|filterShiftLocationFixes/, "采集 / 过滤管线不得再接入任务");
});

test("清理旧版后台定位：幂等、带审核守卫、先停任务再限时上传最后清队列，且永不抛错", () => {
  const retire = sliceBetween(tracking, "export async function retireLegacyAttendanceLocationTracking", "\n}\n");
  assert.match(retire, /if \(isIosReviewSessionActive\(\)\) \{[\s\S]{0,160}return "idle";/);
  // 没有上下文、没有原生任务、队列为空 → 什么都不做。
  assert.match(retire, /if \(!context && !started && queued === 0\) \{\s*return false;/);
  // 顺序：停原生任务（保留队列）→ 限时 flush → 清队列。
  const stopIndex = retire.indexOf("stopAttendanceLocationTrackingKeepingPendingSamples()");
  const flushIndex = retire.indexOf("flushPendingLocationSamplesWithin()");
  const clearIndex = retire.indexOf("clearPendingLocationSamples()");
  assert.ok(stopIndex >= 0 && stopIndex < flushIndex && flushIndex < clearIndex, "必须先停任务，再尽力上传，最后清空队列");
  // 永不抛错：整体被 try/catch 兜住并记日志。
  assert.match(retire, /\} catch \(error\) \{\s*console\.warn\("\[attendance-location\] 清理旧版班中定位失败", error\);\s*return "idle";/);
  // 复用 stop 包装之外的串行器，避免与下班停止并发互相打断。
  assert.match(retire, /runLifecycleSerially\(/);
});

test("下班停止包装：强制停止与审核会话先走 control 原函数（清队列、不 flush），普通下班才限时 flush", () => {
  const stop = sliceBetween(tracking, "export async function stopAttendanceLocationTracking(", "\n}\n");
  assert.match(stop, /options\?\.force \|\| isIosReviewSessionActive\(\)/);
  assert.ok(
    stop.indexOf("stopAttendanceLocationTrackingAndClearQueue(options)")
      < stop.indexOf("flushPendingLocationSamplesWithin()"),
    "强制路径必须在 flush 之前返回",
  );
  assert.match(stop, /stopAttendanceLocationTrackingKeepingPendingSamples\(\)/);
  assert.match(stop, /if \(stopFailed\) \{\s*throw stopError;/, "停止失败仍需把错误交还调用方");
});

test("上传带 iOS 审核会话守卫：运行时装配把 isIosReviewSessionActive 注入 flusher", () => {
  assert.match(sync, /isReviewSessionActive:\s*isIosReviewSessionActive/);
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

test("挂接点：根布局保留任务模块的启动期 import，并挂载前台清理 hook（受 sideEffectsEnabled 控制）", () => {
  assert.match(rootLayout, /import "@\/modules\/attendance\/location-tracking";/);
  assert.match(rootLayout, /useLegacyLocationTrackingCleanup\(\{ enabled: sideEffectsEnabled \}\)/);
  assert.match(cleanupHook, /AppState\.addEventListener\("change"/);
  assert.match(cleanupHook, /state === "active"/);
  assert.match(cleanupHook, /retireLegacyAttendanceLocationTracking/);
  assert.doesNotMatch(cleanupHook, /startLocationUpdatesAsync|startAttendanceLocationTracking/, "清理 hook 不得启动任何定位");
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
    "use-legacy-location-cleanup.ts",
  ]) {
    const source = read(`src/modules/attendance/${file}`);
    assert.doesNotMatch(source, /@\/modules\/attendance\/api"/, `${file} 不应依赖 attendance/api.ts`);
    assert.doesNotMatch(source, /@\/modules\/attendance\/types"/, `${file} 不应依赖 attendance/types.ts`);
  }
});
