import * as Crypto from "expo-crypto";
import * as Location from "expo-location";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { useAuthStore } from "@/store/auth-store";
import {
  FOREGROUND_SHIFT_LOCATION_STORAGE_KEY,
  FOREGROUND_SHIFT_LOCATION_TIMEOUT_MS,
  buildForegroundShiftSampleRequest,
  createForegroundShiftLocationContext,
  isShiftEndedByUploadResult,
  markForegroundShiftLocationAttempt,
  parseForegroundShiftLocationContext,
  resolveForegroundShiftLocationAction,
  type ForegroundShiftLocationContext,
} from "@/modules/attendance/foreground-shift-location";
import { postAttendanceLocationSamplesBatch } from "@/modules/attendance/location-sample-batch-api";
import { resolveCurrentLocationOwnerId } from "@/modules/attendance/location-sample-owner";
import {
  collectOptionalLoginLocationAttempt,
  type OptionalLoginLocationBridge,
} from "@/modules/attendance/optional-login-location";

// 班中「回前台取一次位置」的运行时装配（原生定位 + 存储 + 上传）。判定规则在 foreground-shift-location.ts。
// 这里刻意只用「读权限状态 + 前台取一次位置」：不弹权限框、不启动任何后台定位，
// 由 location-tracking-source-contract.test.ts 钉住。

export type ForegroundShiftLocationOutcome = "skipped" | "cleared" | "uploaded" | "failed";

async function readContext(): Promise<ForegroundShiftLocationContext | null> {
  try {
    return parseForegroundShiftLocationContext(
      await AppAsyncStorage.getObject(FOREGROUND_SHIFT_LOCATION_STORAGE_KEY),
    );
  } catch {
    return null;
  }
}

async function writeContext(context: ForegroundShiftLocationContext) {
  await AppAsyncStorage.setObject(FOREGROUND_SHIFT_LOCATION_STORAGE_KEY, context);
}

/** 删掉「正在上班」标记。永不抛错：下班 / 登出路径不能被它打断。 */
export async function clearForegroundShiftLocationContext() {
  try {
    await AppAsyncStorage.removeItem(FOREGROUND_SHIFT_LOCATION_STORAGE_KEY);
  } catch (error) {
    console.warn("[attendance-location] 清除班中前台定位标记失败", error);
  }
}

/**
 * 打卡保存成功后调用：上班写下标记（门店、上班时刻、所属用户），下班或信息不全则删掉标记。
 * 永不抛错——打卡已经成功，这里出问题只意味着这一班不会有回前台的位置点。
 */
export async function syncForegroundShiftLocationContext(result: {
  punchType: string;
  storeCode?: string;
  serverTimeUtc?: string;
}) {
  if (isIosReviewSessionActive()) {
    return;
  }
  try {
    const context = createForegroundShiftLocationContext({
      punchType: result.punchType,
      storeCode: result.storeCode,
      serverTimeUtc: result.serverTimeUtc,
      ownerUserId: await resolveCurrentLocationOwnerId(),
    });
    if (context) {
      await writeContext(context);
    } else {
      await clearForegroundShiftLocationContext();
    }
  } catch (error) {
    console.warn("[attendance-location] 同步班中前台定位标记失败", error);
  }
}

const locationBridge: OptionalLoginLocationBridge = {
  getForegroundPermissionsAsync: async () => {
    const permission = await Location.getForegroundPermissionsAsync();
    return { status: permission.status };
  },
  getCurrentPositionAsync: () =>
    Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.High }),
};

async function captureAndUpload(nowMs: number): Promise<ForegroundShiftLocationOutcome> {
  // 没上班的人（绝大多数时候）到这里只花一次存储读取。
  const context = await readContext();
  if (!context) {
    return "skipped";
  }
  const action = resolveForegroundShiftLocationAction({
    context,
    currentUserId: await resolveCurrentLocationOwnerId(),
    nowMs,
  });
  if (action === "none") {
    return "skipped";
  }
  if (action === "clear") {
    await clearForegroundShiftLocationContext();
    return "cleared";
  }

  // 先记下「已尝试」再取位置：取失败 / 超时也要等满间隔，避免反复唤起定位耗电。
  await writeContext(markForegroundShiftLocationAttempt(context, nowMs));

  const attempt = await collectOptionalLoginLocationAttempt({
    bridge: locationBridge,
    timeoutMs: FOREGROUND_SHIFT_LOCATION_TIMEOUT_MS,
  });
  if (!attempt.location) {
    // 未授权 / 定位服务不可用 / 超时：静默放弃，不提示员工。
    if (attempt.timedOut || attempt.error) {
      console.warn("[attendance-location] 回前台取位置未成功", attempt.timedOut ? "timeout" : attempt.error);
    }
    return "failed";
  }

  const clientSampleId = Crypto.randomUUID();
  const result = await postAttendanceLocationSamplesBatch(
    buildForegroundShiftSampleRequest({ context, fix: attempt.location, clientSampleId }),
  );
  if (isShiftEndedByUploadResult(result, clientSampleId)) {
    // 服务端说该时刻没有有效班次（被调整 / 补卡 / 在别处下班）：不再继续取。
    await clearForegroundShiftLocationContext();
    return "cleared";
  }
  return "uploaded";
}

let inFlight: Promise<ForegroundShiftLocationOutcome> | null = null;

/**
 * 回前台时尝试取一次位置并上传。单飞、永不抛错：
 * - 未登录 / iOS 审核会话 / 没有「正在上班」标记 / 未到间隔：什么都不做；
 * - 失败（无权限、超时、网络）不重试、不入队，等下次回前台。
 */
export function runForegroundShiftLocationSample(nowMs: number = Date.now()): Promise<ForegroundShiftLocationOutcome> {
  if (inFlight) {
    return inFlight;
  }
  if (isIosReviewSessionActive() || !useAuthStore.getState().isAuthenticated) {
    return Promise.resolve("skipped");
  }
  const running = captureAndUpload(nowMs)
    .catch((error: unknown): ForegroundShiftLocationOutcome => {
      console.warn("[attendance-location] 回前台取位置上传失败", error);
      return "failed";
    })
    .finally(() => {
      inFlight = null;
    });
  inFlight = running;
  return running;
}
