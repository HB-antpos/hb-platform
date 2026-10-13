import * as Location from "expo-location";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import type { LastKeptLocationPoint } from "@/modules/attendance/location-sample-filter";
import { locationSampleQueue } from "@/modules/attendance/location-sample-queue";

export const ATTENDANCE_LOCATION_TASK = "hbweb-attendance-location-sample";
export const ACTIVE_SHIFT_STORAGE_KEY = "hbweb_attendance_active_shift_location";
/** Android 前台服务的定位时间间隔（仅 Android 生效；distanceInterval 保持 0，由过滤函数决定保留哪些点）。 */
export const ANDROID_LOCATION_TIME_INTERVAL_MS = 60 * 1000;
/** iOS deferredUpdates 的批量回调间隔（仅 iOS 生效）；拆成独立常量方便日后真机调参。 */
export const IOS_DEFERRED_UPDATES_INTERVAL_MS = 5 * 60 * 1000;

export interface ActiveShiftLocationContext {
  storeCode: string;
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  /** 旧版 20 分钟节流使用；新逻辑不再读写，仅为兼容旧版本写下的上下文而保留。 */
  lastUploadedAtUtc?: string;
  /** 上班打卡时登录的用户（统一小写 GUID）；入队的点都绑定到该用户。旧版本启动的班次可能没有。 */
  ownerUserId?: string;
  /** 上一个保留点：采集过滤以它为基准，跨回调、跨进程、跨自愈重启延续。 */
  lastKeptPoint?: LastKeptLocationPoint;
}

/** 班中后台定位任务的启动选项；上班启动与自愈重启必须使用同一份。 */
export function buildAttendanceLocationTaskOptions(): Location.LocationTaskOptions {
  return {
    accuracy: Location.Accuracy.Balanced,
    // Android：每 60 秒一个定位；iOS 忽略 timeInterval。
    timeInterval: ANDROID_LOCATION_TIME_INTERVAL_MS,
    distanceInterval: 0,
    // iOS 后台定位使用 deferredUpdatesInterval 才能按节奏批量回调；Android 忽略这两项。
    deferredUpdatesInterval: IOS_DEFERRED_UPDATES_INTERVAL_MS,
    deferredUpdatesDistance: 0,
    pausesUpdatesAutomatically: false,
    showsBackgroundLocationIndicator: true,
    foregroundService: {
      notificationTitle: "HB attendance location",
      notificationBody: "Location is sampled while you are clocked in.",
    },
  };
}

export async function readActiveShiftContext() {
  if (isIosReviewSessionActive()) {
    return null;
  }
  return AppAsyncStorage.getObject<ActiveShiftLocationContext>(
    ACTIVE_SHIFT_STORAGE_KEY,
  );
}

export async function writeActiveShiftContext(context: ActiveShiftLocationContext) {
  if (isIosReviewSessionActive()) {
    return;
  }
  await AppAsyncStorage.setObject(ACTIVE_SHIFT_STORAGE_KEY, context);
}

/** 清空尚未上传的班中定位队列（登出、会话失效、设备重置等强制停止路径使用）。 */
export async function clearPendingLocationSamples() {
  await locationSampleQueue.clear();
}

async function stopTrackingTask(options: { clearPendingSamples: boolean }) {
  // 先清本地班中定位上下文，再停止原生后台任务，避免继续带旧凭证上传定位。
  await AppAsyncStorage.removeItem(ACTIVE_SHIFT_STORAGE_KEY);
  if (options.clearPendingSamples) {
    // 队列清理失败不能阻止后面的原生任务停止，所以单独兜住。
    await clearPendingLocationSamples().catch((error) => {
      console.warn("[attendance-location] 清空本地定位队列失败", error);
    });
  }

  const hasStarted = await Location.hasStartedLocationUpdatesAsync(
    ATTENDANCE_LOCATION_TASK,
  );
  if (hasStarted) {
    await Location.stopLocationUpdatesAsync(ATTENDANCE_LOCATION_TASK);
  }
}

/**
 * 停止班中定位，但保留本地尚未上传的轨迹队列，供下次回前台/上班时补传。
 * 只给“正常下班”和“窗口到期”使用（它们仍持有有效凭证）；登出/会话失效必须走
 * stopAttendanceLocationTracking，那条路径会同时清空队列。
 */
export async function stopAttendanceLocationTrackingKeepingPendingSamples() {
  if (isIosReviewSessionActive()) {
    // 审核会话从未启动真实后台定位，也不能改写普通设备的持久化上下文。
    return;
  }
  await stopTrackingTask({ clearPendingSamples: false });
}

export async function stopAttendanceLocationTracking(options?: { force?: boolean }) {
  if (isIosReviewSessionActive() && !options?.force) {
    // 审核会话从未启动真实后台定位，退出时也不能改写普通设备的持久化上下文。
    return;
  }
  // 登出/会话失效/设备重置都会走到这里：这些路径已没有可用凭证，且轨迹属于上一个会话，
  // 所以连同尚未上传的队列一并清掉（隐私），而不是留给下一个登录的人。
  await stopTrackingTask({ clearPendingSamples: true });
}
