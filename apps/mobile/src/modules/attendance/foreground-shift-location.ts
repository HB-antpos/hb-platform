import {
  buildLocationSampleBatchRequest,
  type AttendanceLocationSampleBatchRequest,
  type AttendanceLocationSampleBatchResult,
  type QueuedLocationSample,
} from "@/modules/attendance/location-sample-batch";

// 班中「回前台取一次位置」的纯逻辑：判定要不要取、构造上传请求。
// 不引入任何原生模块 / apiClient，便于在 Node 下做单元测试；真正取位置与发请求的装配在 foreground-shift-location-runtime.ts。
//
// 口径（与「取消班中后台定位」一致，只是多了一个前台时机）：
// - 只在员工已上班、尚未下班期间，App 打开着（冷启动 / 从后台回到前台）时取一次；
// - 只读权限状态，已授予「使用 App 期间」才取，绝不弹系统权限框；
// - 同一员工相邻两次尝试至少相隔 10 分钟；
// - 取一个点就上传一个点，不在手机上留队列、不补传——失败就丢，下次回前台再取。

/** 上班那一刻写下的「正在上班」标记所用的存储键（与旧版班中后台定位的键不同，旧任务清理不会误删它）。 */
export const FOREGROUND_SHIFT_LOCATION_STORAGE_KEY = "hbweb_attendance_foreground_shift_location";
/** 相邻两次尝试的最小间隔。 */
export const FOREGROUND_SHIFT_LOCATION_MIN_INTERVAL_MS = 10 * 60 * 1000;
/** 标记的最长有效期：上班后超过 10 小时视为已失效（服务端同样按 10 小时窗口判定班次）。 */
export const FOREGROUND_SHIFT_LOCATION_MAX_SHIFT_MS = 10 * 60 * 60 * 1000;
/** 单次取位置的总时限；超时即放弃本次。 */
export const FOREGROUND_SHIFT_LOCATION_TIMEOUT_MS = 10 * 1000;
/** 上传时的事件类型：Web「班中定位样本」抽屉按它归入班中样本。 */
export const FOREGROUND_SHIFT_LOCATION_EVENT_TYPE = "ShiftSample" as const;
/** 设备时钟允许比服务端时间慢的幅度；超过它说明设备时钟明显不对，本次不取。 */
const CLOCK_SKEW_TOLERANCE_MS = 5 * 60 * 1000;

/** 服务端在样本不在有效班次内时返回的错误码：说明班次已结束，应清掉标记。 */
export const FOREGROUND_SHIFT_LOCATION_ACTIVE_SHIFT_REQUIRED = "ACTIVE_SHIFT_REQUIRED";

export interface ForegroundShiftLocationContext {
  /** 上班打卡时登录的用户（统一小写 GUID）；换人登录后不再用这份标记。 */
  ownerUserId: string;
  storeCode: string;
  /** 上班打卡的服务端时间（ISO UTC）。 */
  clockedInAtUtc: string;
  /** 上一次尝试取位置的时间（含失败、超时），用来限频。 */
  lastAttemptAtUtc?: string;
}

export type ForegroundShiftLocationAction = "none" | "clear" | "capture";

function normalizeOwnerId(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const normalized = value.trim().toLowerCase();
  return normalized ? normalized : undefined;
}

function parseUtcMs(value: unknown): number | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

/** 上班打卡成功后生成标记；下班打卡、信息不全、时间无法解析都返回 null（调用方据此清掉旧标记）。 */
export function createForegroundShiftLocationContext(input: {
  punchType: string;
  storeCode: string | undefined;
  serverTimeUtc: string | undefined;
  ownerUserId: string | undefined;
}): ForegroundShiftLocationContext | null {
  if (input.punchType !== "ClockIn") {
    return null;
  }
  const storeCode = input.storeCode?.trim();
  const ownerUserId = normalizeOwnerId(input.ownerUserId);
  const clockedInMs = parseUtcMs(input.serverTimeUtc);
  if (!storeCode || !ownerUserId || clockedInMs === undefined) {
    return null;
  }
  return {
    ownerUserId,
    storeCode,
    clockedInAtUtc: new Date(clockedInMs).toISOString(),
  };
}

/** 校验从存储读出来的标记；形状不对一律当作没有。 */
export function parseForegroundShiftLocationContext(raw: unknown): ForegroundShiftLocationContext | null {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    return null;
  }
  const record = raw as Record<string, unknown>;
  const ownerUserId = normalizeOwnerId(record.ownerUserId);
  const storeCode = typeof record.storeCode === "string" ? record.storeCode.trim() : "";
  const clockedInMs = parseUtcMs(record.clockedInAtUtc);
  if (!ownerUserId || !storeCode || clockedInMs === undefined) {
    return null;
  }
  const lastAttemptMs = parseUtcMs(record.lastAttemptAtUtc);
  return {
    ownerUserId,
    storeCode,
    clockedInAtUtc: new Date(clockedInMs).toISOString(),
    ...(lastAttemptMs !== undefined ? { lastAttemptAtUtc: new Date(lastAttemptMs).toISOString() } : {}),
  };
}

/**
 * 判定这次回前台要做什么：
 * - none：什么都不做（没有标记 / 还没到间隔 / 设备时钟明显不对）；
 * - clear：标记已不能用（换了账号 / 已超过 10 小时），调用方应把它删掉；
 * - capture：取一次位置。
 */
export function resolveForegroundShiftLocationAction(input: {
  context: ForegroundShiftLocationContext | null;
  currentUserId: string | undefined;
  nowMs: number;
  minIntervalMs?: number;
  maxShiftMs?: number;
}): ForegroundShiftLocationAction {
  const { context, nowMs } = input;
  if (!context) {
    return "none";
  }
  if (normalizeOwnerId(input.currentUserId) !== context.ownerUserId) {
    return "clear";
  }
  const clockedInMs = parseUtcMs(context.clockedInAtUtc);
  if (clockedInMs === undefined) {
    return "clear";
  }
  const elapsedMs = nowMs - clockedInMs;
  if (elapsedMs >= (input.maxShiftMs ?? FOREGROUND_SHIFT_LOCATION_MAX_SHIFT_MS)) {
    return "clear";
  }
  if (elapsedMs < -CLOCK_SKEW_TOLERANCE_MS) {
    return "none";
  }
  const lastAttemptMs = parseUtcMs(context.lastAttemptAtUtc);
  if (
    lastAttemptMs !== undefined
    && nowMs >= lastAttemptMs
    && nowMs - lastAttemptMs < (input.minIntervalMs ?? FOREGROUND_SHIFT_LOCATION_MIN_INTERVAL_MS)
  ) {
    return "none";
  }
  return "capture";
}

/** 决定取位置之后立刻写回「已尝试」时间：即使取位置失败 / 超时，也要等满间隔才会再取，避免反复耗电。 */
export function markForegroundShiftLocationAttempt(
  context: ForegroundShiftLocationContext,
  nowMs: number,
): ForegroundShiftLocationContext {
  return { ...context, lastAttemptAtUtc: new Date(nowMs).toISOString() };
}

export interface ForegroundShiftLocationFix {
  locationLatitude: number;
  locationLongitude: number;
  locationAccuracy?: number;
  locationCapturedAtUtc: string;
}

/** 把取到的一个点构造成批量上传请求（复用既有批量接口，按 clientSampleId 幂等）。 */
export function buildForegroundShiftSampleRequest(input: {
  context: ForegroundShiftLocationContext;
  fix: ForegroundShiftLocationFix;
  clientSampleId: string;
}): AttendanceLocationSampleBatchRequest {
  const sample: QueuedLocationSample = {
    clientSampleId: input.clientSampleId,
    ownerUserId: input.context.ownerUserId,
    storeCode: input.context.storeCode,
    eventType: FOREGROUND_SHIFT_LOCATION_EVENT_TYPE,
    locationLatitude: input.fix.locationLatitude,
    locationLongitude: input.fix.locationLongitude,
    ...(typeof input.fix.locationAccuracy === "number" ? { locationAccuracy: input.fix.locationAccuracy } : {}),
    locationCapturedAtUtc: input.fix.locationCapturedAtUtc,
  };
  return buildLocationSampleBatchRequest([sample]);
}

/** 上传结果里服务端明确说「该时刻没有有效班次」→ 班次已结束（被调整 / 补卡 / 在别处下班），标记应清掉。 */
export function isShiftEndedByUploadResult(
  result: AttendanceLocationSampleBatchResult,
  clientSampleId: string,
): boolean {
  return result.rejected.some(
    (entry) =>
      entry.clientSampleId === clientSampleId
      && entry.errorCode === FOREGROUND_SHIFT_LOCATION_ACTIVE_SHIFT_REQUIRED,
  );
}
