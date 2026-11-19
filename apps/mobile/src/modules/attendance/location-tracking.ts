import * as Location from "expo-location";
import * as TaskManager from "expo-task-manager";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import {
  ATTENDANCE_LOCATION_TASK,
  clearPendingLocationSamples,
  readActiveShiftContext,
  stopAttendanceLocationTracking as stopAttendanceLocationTrackingAndClearQueue,
  stopAttendanceLocationTrackingKeepingPendingSamples,
} from "@/modules/attendance/location-tracking-control";
import { createSerialRunner, locationSampleQueue } from "@/modules/attendance/location-sample-queue";
import { flushPendingLocationSamplesWithin } from "@/modules/attendance/location-sample-sync";

// 班中后台定位已取消：App 只在扫码上班 / 下班那一刻读取位置（见 use-punch-verification.ts），
// 不再启动任何后台定位任务，也不再申请「始终允许」。
// 本模块只剩两件事：
// 1. 收掉旧版本留下的后台任务（员工升级前已经上班、原生任务仍在跑），并清掉本机残留的班次上下文与待传队列；
// 2. 下班打卡时的停止（同样兜底清理旧任务）。
// 这里刻意不出现任何「启动后台定位 / 申请后台权限」的调用，由 location-tracking-source-contract.test.ts 钉住。

// 停止 / 清理互相串行，避免前台恢复与下班停止并发时互相打断。
const runLifecycleSerially = createSerialRunner();

/**
 * 收掉旧版本留下的班中后台定位。幂等、永不抛错，可以放心在每次回前台时调用：
 * - 没有班次上下文、没有在跑的原生任务、本地队列也为空 → 什么都不做（只有两次原生读取 + 一次存储读取）；
 * - 否则先停原生任务并清班次上下文（此后不会再有新点），再在 8 秒上限内尽力把剩余点传完，
 *   最后清空本地队列——已不再有任何功能依赖这些点，不留在员工手机里。
 */
export async function retireLegacyAttendanceLocationTracking(): Promise<"idle" | "retired"> {
  if (isIosReviewSessionActive()) {
    // 审核会话从未启动真实后台定位，也不能改写普通设备的持久化数据。
    return "idle";
  }
  try {
    const hadLegacyState = await runLifecycleSerially(async () => {
      const [context, started, queued] = await Promise.all([
        readActiveShiftContext(),
        Location.hasStartedLocationUpdatesAsync(ATTENDANCE_LOCATION_TASK),
        locationSampleQueue.size().catch(() => 0),
      ]);
      if (!context && !started && queued === 0) {
        return false;
      }
      await stopAttendanceLocationTrackingKeepingPendingSamples();
      return true;
    });
    if (!hadLegacyState) {
      return "idle";
    }
    // 上传失败 / 超时都不影响清理：flush 永不 reject，超时只是放弃等待。
    await flushPendingLocationSamplesWithin();
    await clearPendingLocationSamples();
    return "retired";
  } catch (error) {
    console.warn("[attendance-location] 清理旧版班中定位失败", error);
    return "idle";
  }
}

if (!TaskManager.isTaskDefined(ATTENDANCE_LOCATION_TASK)) {
  // 旧版本启动的原生任务在系统回调时会进到这里：不再采集，直接把任务收掉。
  // 即使员工一直没有回到前台，只要系统再唤醒一次，旧任务也会自行结束。
  TaskManager.defineTask(ATTENDANCE_LOCATION_TASK, async () => {
    if (isIosReviewSessionActive()) {
      return;
    }
    await retireLegacyAttendanceLocationTracking();
  });
}

/**
 * 下班停止（兜底清理旧版本留下的班中定位）。
 * - 普通下班：先停原生任务并清班次上下文，再在 8 秒上限内尽力把队列剩余点批量上传；
 *   上传失败/超时不影响停止结果。
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
  // 即使停止原生任务出错，也照样尽力上传，最后再把停止的错误交还调用方。
  await flushPendingLocationSamplesWithin();
  if (stopFailed) {
    throw stopError;
  }
}
