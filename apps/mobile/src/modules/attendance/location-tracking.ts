import * as Crypto from "expo-crypto";
import * as Location from "expo-location";
import * as TaskManager from "expo-task-manager";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import {
  isAttendanceLocationWindowExpired,
  type AttendanceLocationTrackingWindow,
} from "@/modules/attendance/location-tracking-window";
import {
  ATTENDANCE_LOCATION_TASK,
  buildAttendanceLocationTaskOptions,
  readActiveShiftContext,
  stopAttendanceLocationTracking as stopAttendanceLocationTrackingAndClearQueue,
  stopAttendanceLocationTrackingKeepingPendingSamples,
  writeActiveShiftContext,
  type ActiveShiftLocationContext,
} from "@/modules/attendance/location-tracking-control";
import { captureShiftLocationFixes } from "@/modules/attendance/location-sample-capture";
import { shouldFlushAfterEnqueue } from "@/modules/attendance/location-sample-flush";
import type { RawLocationFix } from "@/modules/attendance/location-sample-filter";
import { resolveCurrentLocationOwnerId } from "@/modules/attendance/location-sample-owner";
import { createSerialRunner, locationSampleQueue } from "@/modules/attendance/location-sample-queue";
import {
  flushPendingLocationSamples,
  flushPendingLocationSamplesWithin,
  locationSampleFlusher,
} from "@/modules/attendance/location-sample-sync";

// 班中定位：上班打卡后启动原生后台任务，回调里逐点过滤后写入本地持久队列，
// 再批量上传（可补传）；下班打卡停止任务。登出/会话失效等强制停止路径见 location-tracking-control.ts。

/** 班次上下文在存储里还带着窗口字段（startedAtUtc/workDate/storeTimeZone），读取时统一成这个形状。 */
type StoredShiftContext = ActiveShiftLocationContext & AttendanceLocationTrackingWindow;

async function readStoredShiftContext() {
  return (await readActiveShiftContext()) as StoredShiftContext | null;
}

function createSampleId() {
  try {
    return Crypto.randomUUID();
  } catch {
    // clientSampleId 要求 GUID 形状（≤50 字符）；原生随机数不可用时退回 Math.random 拼出合法 UUID v4 形状。
    const hex = (length: number) =>
      Array.from({ length }, () => Math.floor(Math.random() * 16).toString(16)).join("");
    return `${hex(8)}-${hex(4)}-4${hex(3)}-a${hex(3)}-${hex(12)}`;
  }
}

function toRawFix(location: Location.LocationObject): RawLocationFix {
  return {
    timestamp: location.timestamp,
    latitude: location.coords.latitude,
    longitude: location.coords.longitude,
    accuracy: location.coords.accuracy,
    // 仅 Android 有 mocked 字段；缺失时不带 isMocked。
    mocked: location.mocked,
  };
}

// 后台任务回调里的“读上下文 → 过滤 → 入队 → 写回上一个保留点”必须串行，
// 否则两次相邻回调会基于同一个旧的上一个保留点各自过滤，造成重复保留或基准回退。
const runCaptureSerially = createSerialRunner();
// 启动 / 停止 / 自愈重启互相串行，避免自愈刚重启任务时被并发的停止打乱。
const runLifecycleSerially = createSerialRunner();

async function processLocationTaskData(data: unknown) {
  const context = await readStoredShiftContext();
  if (!context?.storeCode) {
    return null;
  }
  const locations = (data as { locations?: Location.LocationObject[] } | undefined)?.locations;
  return captureShiftLocationFixes<StoredShiftContext>(
    {
      queue: locationSampleQueue,
      readContext: readStoredShiftContext,
      writeContext: writeActiveShiftContext,
      resolveOwnerUserId: resolveCurrentLocationOwnerId,
      createSampleId,
      log: (message, detail) => console.warn(`[attendance-location] ${message}`, detail),
    },
    context,
    (locations ?? []).map(toRawFix),
    new Date().toISOString(),
  );
}

if (!TaskManager.isTaskDefined(ATTENDANCE_LOCATION_TASK)) {
  TaskManager.defineTask(ATTENDANCE_LOCATION_TASK, async ({ data, error }) => {
    if (isIosReviewSessionActive()) {
      // 审核会话即使收到遗留系统回调，也不得读取上下文、入队或上传位置样本。
      return;
    }
    if (error) {
      console.warn("[attendance-location] 后台定位任务失败", error);
      return;
    }

    let result: Awaited<ReturnType<typeof processLocationTaskData>>;
    try {
      result = await runCaptureSerially(() => processLocationTaskData(data));
    } catch (captureError) {
      console.warn("[attendance-location] 处理班中定位回调失败", captureError);
      return;
    }
    if (!result) {
      return;
    }

    if (result.shouldStop) {
      // 系统回调可能晚于截止点：窗口内的点已入队，先停原生任务（保留队列），截止点之后的坐标已被过滤掉。
      try {
        await runLifecycleSerially(() => stopAttendanceLocationTrackingKeepingPendingSamples());
      } catch (stopError) {
        console.warn("[attendance-location] 窗口到期后停止后台定位失败", stopError);
      }
    }

    // 触发上传：队列 ≥ 5 点，或距上次尝试 ≥ 5 分钟；窗口到期时这是最后一次机会，忽略退避。
    const queueSize = result.queueSize ?? (await locationSampleQueue.size().catch(() => 0));
    if (
      queueSize > 0
      && (result.shouldStop
        || shouldFlushAfterEnqueue({
          queueSize,
          lastAttemptAtMs: locationSampleFlusher.getLastAttemptAtMs(),
          nowMs: Date.now(),
        }))
    ) {
      await flushPendingLocationSamples({ ignoreBackoff: result.shouldStop });
    }
  });
}

export async function ensureAttendanceBackgroundLocationPermission() {
  if (isIosReviewSessionActive()) {
    return true;
  }

  if (await hasAttendanceBackgroundLocationPermission()) {
    return true;
  }

  const foreground = await Location.requestForegroundPermissionsAsync();
  if (foreground.status !== "granted") {
    return false;
  }

  const background = await Location.requestBackgroundPermissionsAsync();
  return background.status === "granted";
}

export async function hasAttendanceBackgroundLocationPermission() {
  const foreground = await Location.getForegroundPermissionsAsync();
  if (foreground.status !== "granted") {
    return false;
  }
  const background = await Location.getBackgroundPermissionsAsync();
  return background.status === "granted";
}

export async function startAttendanceLocationTracking(
  context: Omit<ActiveShiftLocationContext, "lastUploadedAtUtc" | "lastKeptPoint" | "ownerUserId">
    & AttendanceLocationTrackingWindow,
) {
  if (isIosReviewSessionActive()) {
    // 审核模式仅模拟班中定位状态，不写本地任务上下文或启动后台定位。
    return;
  }
  await runLifecycleSerially(async () => {
    // 新班次从全新的上下文开始（没有上一个保留点，第一个点必定保留），
    // 并把上班时的登录用户记进去：之后入队的点都绑定到这个用户。
    const ownerUserId = await resolveCurrentLocationOwnerId();
    await writeActiveShiftContext({
      ...context,
      ...(ownerUserId ? { ownerUserId } : {}),
    });

    const hasStarted = await Location.hasStartedLocationUpdatesAsync(
      ATTENDANCE_LOCATION_TASK,
    );
    if (hasStarted) {
      return;
    }

    await Location.startLocationUpdatesAsync(
      ATTENDANCE_LOCATION_TASK,
      buildAttendanceLocationTaskOptions(),
    );
  });

  // 顺带把上次下班/离线遗留的点补传出去；不阻塞上班打卡流程（flush 永不 reject）。
  void flushPendingLocationSamples({ ignoreBackoff: true });
}

/**
 * 下班停止班中定位。
 * - 普通下班：先停原生任务并清班次上下文（此后不会再有新点入队，也不会把下班之后的点混进来），
 *   再在 8 秒上限内尽力把队列剩余点批量上传；上传失败/超时不影响停止结果，剩余点保留到下次
 *   回前台或上班时补传（后端按相对采集时刻判定班次，下班后补传也被接受）。
 * - 强制停止（force，登出/会话失效/审核）与审核会话：不 flush（已无可用凭证且涉及隐私），
 *   直接走 control 里的原函数，它会连同本地队列一起清空。
 */
export async function stopAttendanceLocationTracking(options?: { force?: boolean }) {
  if (options?.force || isIosReviewSessionActive()) {
    await stopAttendanceLocationTrackingAndClearQueue(options);
    return;
  }

  let stopError: unknown;
  let stopFailed = false;
  try {
    await runLifecycleSerially(() => stopAttendanceLocationTrackingKeepingPendingSamples());
  } catch (error) {
    stopFailed = true;
    stopError = error;
  }
  // 即使停止原生任务出错，也照样尽力上传，最后再把停止的错误交还调用方（与旧行为一致）。
  await flushPendingLocationSamplesWithin();
  if (stopFailed) {
    throw stopError;
  }
}

export type AttendanceLocationHealth =
  | "skipped"
  | "inactive"
  | "healthy"
  | "restarted"
  | "expired"
  | "permission-missing";

/**
 * 自愈：存在班次上下文、窗口未过期、后台权限仍在，但原生任务已不在运行
 * （系统回收、Android 厂商杀后台、重启等）时，用同样的选项重启任务，且不重置上一个保留点。
 * 窗口已过期则停止任务并清理上下文（队列保留，等待补传）。
 */
export async function ensureAttendanceLocationTrackingHealthy(): Promise<AttendanceLocationHealth> {
  if (isIosReviewSessionActive()) {
    return "skipped";
  }
  return runLifecycleSerially(async (): Promise<AttendanceLocationHealth> => {
    const context = await readStoredShiftContext();
    if (!context?.storeCode) {
      return "inactive";
    }
    if (isAttendanceLocationWindowExpired(context, new Date().toISOString())) {
      await stopAttendanceLocationTrackingKeepingPendingSamples();
      return "expired";
    }
    if (await Location.hasStartedLocationUpdatesAsync(ATTENDANCE_LOCATION_TASK)) {
      return "healthy";
    }
    if (!(await hasAttendanceBackgroundLocationPermission())) {
      // 权限被收回时无法重启；保留上下文，等用户重新授权后下次回前台再试。
      return "permission-missing";
    }

    await Location.startLocationUpdatesAsync(
      ATTENDANCE_LOCATION_TASK,
      buildAttendanceLocationTaskOptions(),
    );
    if (!(await readStoredShiftContext())) {
      // 重启期间用户刚好下班/登出（上下文已被清掉）：刚启动的任务没有归属，立即停掉，
      // 否则会留下一个没有上下文、持续占用前台服务通知的空转任务。
      await Location.stopLocationUpdatesAsync(ATTENDANCE_LOCATION_TASK).catch(() => undefined);
      return "inactive";
    }
    return "restarted";
  });
}

/**
 * App 回到前台（或冷启动完成）时调用：先自愈定位任务，再把本地队列剩余的点批量补传。
 * 任何一步失败都只记日志，不向外抛。
 */
export async function recoverAttendanceLocationTracking() {
  if (isIosReviewSessionActive()) {
    return;
  }
  try {
    await ensureAttendanceLocationTrackingHealthy();
  } catch (error) {
    console.warn("[attendance-location] 自愈班中定位任务失败", error);
  }
  await flushPendingLocationSamples({ ignoreBackoff: true });
}
