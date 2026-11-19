// 响应位置请求时「取一次位置」的编排（依赖全部由外部注入，不依赖 React Native / 原生模块，可在 Node 下测试）。
//
// 两种模式：
// - 非交互（冷启动 / 回前台 / 前台收到推送）：只有「前台定位权限已经是 granted」才取位置，
//   否则静默跳过——自动触发绝不弹系统权限框（员工没有主动操作就弹框是骚扰）；
// - 交互（员工点了位置请求通知）：走 collectRequiredLocation()，允许弹系统权限框。
//
// 只用「使用 App 期间」的前台权限、高精度单点定位（getCurrentPositionAsync，不是缓存的最后已知位置）。
// 不请求后台权限、不启动任何后台任务；iOS 审核态不取真实位置。
// 坐标不写日志。
import type { CapturedLocationPayload } from "@/modules/attendance/required-location";

/** 非交互取点的总期限：超时放弃，释放消费锁，下次触发再试。 */
export const LOCATION_REQUEST_SILENT_CAPTURE_TIMEOUT_MS = 20_000;
/** 交互取点的总期限：要容纳员工看系统权限框并做决定的时间，所以更长。 */
export const LOCATION_REQUEST_INTERACTIVE_CAPTURE_TIMEOUT_MS = 90_000;

/** collectRequiredLocation 在权限被拒时抛出的错误码（RequiredLocationError.code）。 */
const REQUIRED_LOCATION_ERROR_CODE = "LOCATION_REQUIRED";

export interface LocationRequestCaptureBridge {
  /** iOS 审核态：必须零真实定位。 */
  isIosReviewSessionActive(): boolean;
  /** 只读查询前台定位权限，不弹框（expo-location 的 getForegroundPermissionsAsync）。 */
  getForegroundPermissionsAsync(): Promise<{ status: string }>;
  /** 高精度单点定位（Location.Accuracy.High）。 */
  getCurrentPositionAsync(): Promise<{
    coords: { latitude: number; longitude: number; accuracy?: number | null };
    timestamp: number;
  }>;
  /** 交互模式专用：required-location.ts 的 collectRequiredLocation（含权限申请）。 */
  collectRequiredLocation(): Promise<CapturedLocationPayload>;
}

export type LocationCaptureOutcome =
  | { status: "ok"; location: CapturedLocationPayload }
  /** 非交互模式下前台定位权限不是 granted：静默跳过（没有取位置、没有弹框）。 */
  | { status: "permission-not-granted" }
  /** 交互模式下员工拒绝了定位权限。 */
  | { status: "permission-denied" }
  /** 审核态不取真实位置。 */
  | { status: "review-skipped" }
  /** 超时 / 定位服务不可用 / 坐标无效：下次触发可以重试。 */
  | { status: "unavailable"; reason: "timeout" | "error" };

class CaptureTimeoutError extends Error {
  constructor() {
    super("LOCATION_REQUEST_CAPTURE_TIMEOUT");
    this.name = "CaptureTimeoutError";
  }
}

/** 给 promise 加超时：超时后 reject；原 promise 之后的结果 / 异常都被吞掉（不产生未处理的 rejection）。 */
function withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new CaptureTimeoutError()), timeoutMs);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** 非交互：权限已授予才取点。locationCapturedAtUtc 取定位点自带的时间戳（不是缓存旧点，也不是「现在」）。 */
async function captureSilently(bridge: LocationRequestCaptureBridge): Promise<LocationCaptureOutcome> {
  const permission = await bridge.getForegroundPermissionsAsync();
  if (permission.status !== "granted") {
    return { status: "permission-not-granted" };
  }
  const position = await bridge.getCurrentPositionAsync();
  const { latitude, longitude, accuracy } = position.coords;
  if (!isFiniteNumber(latitude) || !isFiniteNumber(longitude) || !isFiniteNumber(position.timestamp)) {
    return { status: "unavailable", reason: "error" };
  }
  return {
    status: "ok",
    location: {
      locationLatitude: latitude,
      locationLongitude: longitude,
      locationAccuracy: isFiniteNumber(accuracy) ? accuracy : undefined,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: new Date(position.timestamp).toISOString(),
    },
  };
}

export async function captureLocationForRequest({
  interactive,
  bridge,
  timeoutMs,
}: {
  interactive: boolean;
  bridge: LocationRequestCaptureBridge;
  /** 不传按模式取默认期限（非交互 20 秒、交互 90 秒）。 */
  timeoutMs?: number;
}): Promise<LocationCaptureOutcome> {
  // 审核态：不读权限、不取位置（collectRequiredLocation 在审核态会给固定演示坐标，但这里根本不应走到上报）。
  if (bridge.isIosReviewSessionActive()) {
    return { status: "review-skipped" };
  }
  const budget = timeoutMs ?? (interactive ? LOCATION_REQUEST_INTERACTIVE_CAPTURE_TIMEOUT_MS : LOCATION_REQUEST_SILENT_CAPTURE_TIMEOUT_MS);
  try {
    if (interactive) {
      const location = await withTimeout(bridge.collectRequiredLocation(), budget);
      return { status: "ok", location };
    }
    return await withTimeout(captureSilently(bridge), budget);
  } catch (error) {
    if (error instanceof CaptureTimeoutError) {
      return { status: "unavailable", reason: "timeout" };
    }
    const code = (error as { code?: unknown } | null)?.code;
    if (interactive && code === REQUIRED_LOCATION_ERROR_CODE) {
      return { status: "permission-denied" };
    }
    return { status: "unavailable", reason: "error" };
  }
}
