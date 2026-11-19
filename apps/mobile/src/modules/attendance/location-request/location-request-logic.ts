// 「服务端要求员工取一次位置」（位置请求）的纯逻辑：契约类型、响应归一化、待办筛选 / 排序、错误分类、请求体构造。
// 不依赖 React Native / 原生模块 / apiClient，可以在 Node 下直接测试。
//
// 背景：店长 / 管理员可以对「已上班」的员工发起位置请求。App 已不再声明后台定位（后台取不到位置），
// 所以服务端只发一条可见推送；员工点通知、回到前台或在前台收到该推送时，App 才检查待办，
// 并在「使用 App 期间」的前台权限下取一次高精度位置上报。
// 接口契约见后端 AttendanceLocationRequestDtos.cs / AttendanceLocationRequestRules.cs。
//
// 隐私约定：坐标只出现在 respond 请求体里，不进日志、不落盘、不进 react-query 缓存。
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import type { CapturedLocationPayload } from "@/modules/attendance/required-location";

// ───────────── 路径与常量 ─────────────

/** apiClient 的 baseURL 已带 /api，路径不再加前缀；与 attendance/api.ts 的 ATTENDANCE_BASE 一致。 */
export const LOCATION_REQUEST_ATTENDANCE_BASE = "/react/v1/attendance";
export const LOCATION_REQUEST_PENDING_PATH = `${LOCATION_REQUEST_ATTENDANCE_BASE}/my/location-requests/pending`;

export function locationRequestRespondPath(requestGuid: string): string {
  return `${LOCATION_REQUEST_ATTENDANCE_BASE}/my/location-requests/${encodeURIComponent(requestGuid)}/respond`;
}

/** 后端 AttendanceLocationRequestRules.ErrorCodes 里与「响应」有关的错误码。 */
export const LOCATION_REQUEST_ERROR_CODES = Object.freeze({
  notFound: "LOCATION_REQUEST_NOT_FOUND",
  expired: "LOCATION_REQUEST_EXPIRED",
  cancelled: "LOCATION_REQUEST_CANCELLED",
  /** 响应到达时员工已下班，请求同时作废。 */
  activeShiftRequired: "ACTIVE_SHIFT_REQUIRED",
  /** 定位采集时间早于请求创建时间：不是为这次请求取的点。 */
  locationStale: "LOCATION_STALE",
  sampleInFuture: "SAMPLE_IN_FUTURE",
  locationRequired: "LOCATION_REQUIRED",
} as const);

/** 终态错误码：服务端已给出确定结论，重试不会改变结果，静默丢弃（只 console.warn 不含坐标的短码）。 */
export const TERMINAL_RESPOND_ERROR_CODES: ReadonlySet<string> = new Set(Object.values(LOCATION_REQUEST_ERROR_CODES));

/** 自动触发（冷启动 / 回前台）拉取待办的最小间隔；交互触发与「刚收到位置请求推送」不受此限。 */
export const LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS = 15_000;

/**
 * 响应里的定位采集时间可以比请求创建时间早多少：与后端 CapturedBeforeRequestTolerance 一致（30 秒）。
 * 更早的点服务端一定拒绝（LOCATION_STALE），客户端提前识别，免得白白把请求消耗成终态。
 */
export const LOCATION_REQUEST_CAPTURED_BEFORE_REQUEST_TOLERANCE_MS = 30_000;

/** 单轮最多处理的请求数：服务端对同一员工限频（同一时刻最多一条待响应），这里只是防御。 */
export const LOCATION_REQUEST_MAX_PER_RUN = 5;

// ───────────── 契约类型 ─────────────

/** 员工待办 / 响应结果里客户端用到的字段（其余字段后端也会返回，这里不需要）。 */
export interface AttendanceLocationRequest {
  requestGuid: string;
  storeCode: string;
  storeName?: string;
  /** 请求人姓名，用于「已按 xxx 的请求上报」的提示。 */
  requestedByName?: string;
  /** Pending | Responded | Expired | Cancelled（后端有效状态）。 */
  status: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  /** 仅响应接口使用：对已响应请求的幂等重放。 */
  alreadyResponded: boolean;
}

/** respond 请求体（后端 RespondAttendanceLocationRequestDto，camelCase）。 */
export interface RespondLocationRequestBody {
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  locationLatitude: number;
  locationLongitude: number;
  locationAccuracy?: number;
  locationPermissionStatus: "granted";
  locationCapturedAtUtc: string;
}

export interface LocationRequestDeviceContext {
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
}

/** 响应格式不符合契约：待办拉取失败 / 响应结果无法确认，按「可稍后重试」处理，不丢弃请求。 */
export class LocationRequestResponseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LocationRequestResponseError";
  }
}

// ───────────── 响应归一化 ─────────────

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readString(raw: Record<string, unknown>, key: string): string {
  const value = raw[key];
  return typeof value === "string" ? value.trim() : "";
}

function readOptionalString(raw: Record<string, unknown>, key: string): string | undefined {
  return readString(raw, key) || undefined;
}

/** 单条请求归一化；缺 requestGuid 视为格式错误。 */
export function normalizeLocationRequest(raw: unknown): AttendanceLocationRequest {
  const payload = unwrapApiEnvelope<unknown>(raw);
  if (!isRecord(payload)) {
    throw new LocationRequestResponseError("位置请求响应格式不正确");
  }
  const requestGuid = readString(payload, "requestGuid");
  if (!requestGuid) {
    throw new LocationRequestResponseError("位置请求响应缺少 requestGuid");
  }
  return {
    requestGuid,
    storeCode: readString(payload, "storeCode"),
    storeName: readOptionalString(payload, "storeName"),
    requestedByName: readOptionalString(payload, "requestedByName"),
    status: readString(payload, "status"),
    createdAtUtc: readString(payload, "createdAtUtc"),
    expiresAtUtc: readString(payload, "expiresAtUtc"),
    alreadyResponded: payload.alreadyResponded === true,
  };
}

/**
 * 待办列表归一化：整体不是数组 → 格式错误（调用方按失败处理，不会误以为「没有待办」）；
 * 列表里个别缺 requestGuid 的条目直接丢弃，不影响其它条目。
 */
export function normalizeLocationRequestList(raw: unknown): AttendanceLocationRequest[] {
  const payload = unwrapApiEnvelope<unknown>(raw);
  if (!Array.isArray(payload)) {
    throw new LocationRequestResponseError("位置请求待办响应不是列表");
  }
  const items: AttendanceLocationRequest[] = [];
  for (const entry of payload) {
    try {
      items.push(normalizeLocationRequest(entry));
    } catch {
      // 单条格式异常只丢这一条
    }
  }
  return items;
}

// ───────────── 待办筛选与排序 ─────────────

function parseTime(value: string): number {
  return value ? Date.parse(value) : Number.NaN;
}

/**
 * 从待办列表里挑出本轮要响应的请求：
 * - 只留仍是 Pending、不是「已响应重放」、本会话里还没处理过（成功或终态失败）的请求；
 * - 用请求自带的 expiresAtUtc 对比设备时钟复核未过期——只是额外保护，最终以服务端为准：
 *   expiresAtUtc 解析不了就保留（交给服务端判断）；设备时钟偏快最多漏掉有效期最后几分钟的请求，不会误报；
 * - 同一 requestGuid 只留一条；按创建时间从早到晚排（早的先过期，先响应），创建时间相同按 requestGuid 稳定排序；
 * - 单轮最多 LOCATION_REQUEST_MAX_PER_RUN 条。
 */
export function selectActionableRequests(
  requests: readonly AttendanceLocationRequest[],
  nowMs: number,
  handledGuids: ReadonlySet<string> = new Set(),
): AttendanceLocationRequest[] {
  const seen = new Set<string>();
  const actionable: AttendanceLocationRequest[] = [];
  for (const request of requests) {
    if (!request.requestGuid || seen.has(request.requestGuid)) continue;
    seen.add(request.requestGuid);
    if (request.status.toLowerCase() !== "pending") continue;
    if (request.alreadyResponded) continue;
    if (handledGuids.has(request.requestGuid)) continue;
    const expiresAt = parseTime(request.expiresAtUtc);
    if (Number.isFinite(expiresAt) && expiresAt <= nowMs) continue;
    actionable.push(request);
  }
  actionable.sort((left, right) => {
    const leftCreated = parseTime(left.createdAtUtc);
    const rightCreated = parseTime(right.createdAtUtc);
    const leftKey = Number.isFinite(leftCreated) ? leftCreated : Number.POSITIVE_INFINITY;
    const rightKey = Number.isFinite(rightCreated) ? rightCreated : Number.POSITIVE_INFINITY;
    if (leftKey !== rightKey) return leftKey < rightKey ? -1 : 1;
    return left.requestGuid < right.requestGuid ? -1 : left.requestGuid > right.requestGuid ? 1 : 0;
  });
  return actionable.slice(0, LOCATION_REQUEST_MAX_PER_RUN);
}

/**
 * 这个点是不是「收到请求之后」取的：采集时间不能比请求创建时间早超过容差（与后端规则一致）。
 * 解析不了时间就放行（服务端是最终裁判）。
 * 注意比较的是请求自带的服务端创建时间，而不是设备时钟：GPS 点的时间戳可能是卫星时间，和设备时钟有偏差。
 */
export function isLocationFreshForRequest(
  locationCapturedAtUtc: string,
  request: Pick<AttendanceLocationRequest, "createdAtUtc">,
): boolean {
  const capturedAt = parseTime(locationCapturedAtUtc);
  const createdAt = parseTime(request.createdAtUtc);
  if (!Number.isFinite(capturedAt) || !Number.isFinite(createdAt)) return true;
  return capturedAt >= createdAt - LOCATION_REQUEST_CAPTURED_BEFORE_REQUEST_TOLERANCE_MS;
}

// ───────────── 错误分类 ─────────────

/**
 * terminal：服务端已给出确定结论，本会话不再重试该请求，静默丢弃；
 * retryable：网络错误 / 超时 / 5xx / 响应格式异常，下次触发时可以再试（不自己循环重试）；
 * aborted：账号切换打断，不记日志、不当作失败。
 */
export type LocationRequestFailureKind = "terminal" | "retryable" | "aborted";

export interface LocationRequestFailure {
  kind: LocationRequestFailureKind;
  /** 业务错误码（仅限终态且服务端给出的短码），用于 console.warn；不含任何坐标 / 文案。 */
  code?: string;
  /** HTTP 状态码（有响应时）。 */
  status?: number;
}

function readErrorCode(error: unknown): string | undefined {
  if (!isRecord(error) && !(error instanceof Error)) return undefined;
  const record = error as unknown as Record<string, unknown>;
  if (typeof record.code === "string" && record.code.trim()) return record.code.trim();
  const response = isRecord(record.response) ? record.response : null;
  const data = response && isRecord(response.data) ? response.data : null;
  const fromBody = data?.errorCode ?? data?.ErrorCode;
  return typeof fromBody === "string" && fromBody.trim() ? fromBody.trim() : undefined;
}

function readHttpStatus(error: unknown): number | undefined {
  if (!isRecord(error) && !(error instanceof Error)) return undefined;
  const response = (error as unknown as Record<string, unknown>).response;
  const status = isRecord(response) ? response.status : undefined;
  return typeof status === "number" ? status : undefined;
}

export function classifyLocationRequestError(error: unknown): LocationRequestFailure {
  const code = readErrorCode(error);
  if (code === "ACCOUNT_SESSION_CHANGED") return { kind: "aborted" };
  const status = readHttpStatus(error);
  if (code && TERMINAL_RESPOND_ERROR_CODES.has(code)) return { kind: "terminal", code, status };
  // 收到 success:false 的业务信封：服务端已给出确定结论（含本客户端不认识的新错误码），重试同样会失败。
  if (isRecord(error) || error instanceof Error) {
    if ((error as unknown as Record<string, unknown>).apiBusinessError === true) {
      return { kind: "terminal", code, status };
    }
  }
  // 没有 HTTP 响应：网络中断 / 超时 / 响应格式异常 / 本地超时 → 稍后可重试。
  if (status === undefined) return { kind: "retryable" };
  // 5xx、请求超时、限流是暂时性的；其余 4xx（403 / 404 / 400 …）重试不会变，当作终态。
  if (status >= 500 || status === 408 || status === 429) return { kind: "retryable", status };
  return { kind: "terminal", code, status };
}

// ───────────── 请求体 ─────────────

function trimmedOrUndefined(value?: string): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

/**
 * 构造 respond 请求体：locationCapturedAtUtc 原样用取到点的时间（调用方保证不是缓存旧点）；
 * 可选字段缺失时不出现在请求体里；locationPermissionStatus 固定 granted（只需「使用 App 期间」权限）。
 * 不发送 isMocked：打卡链路同样不带该字段，前台单点也读不到可靠的模拟定位标记。
 */
export function buildRespondRequestBody(
  device: LocationRequestDeviceContext,
  location: CapturedLocationPayload,
): RespondLocationRequestBody {
  const hardwareId = trimmedOrUndefined(device.hardwareId);
  const systemDeviceNumber = trimmedOrUndefined(device.systemDeviceNumber);
  const deviceSystem = trimmedOrUndefined(device.deviceSystem);
  return {
    ...(hardwareId ? { hardwareId } : {}),
    ...(systemDeviceNumber ? { systemDeviceNumber } : {}),
    ...(deviceSystem ? { deviceSystem } : {}),
    locationLatitude: location.locationLatitude,
    locationLongitude: location.locationLongitude,
    ...(typeof location.locationAccuracy === "number" && Number.isFinite(location.locationAccuracy)
      ? { locationAccuracy: location.locationAccuracy }
      : {}),
    locationPermissionStatus: "granted",
    locationCapturedAtUtc: location.locationCapturedAtUtc,
  };
}

// ───────────── 会话资格 ─────────────

/** 资格判断所需的登录态快照（由运行时从 auth-store 读出）。 */
export interface LocationRequestSessionSnapshot {
  isAuthenticated: boolean;
  /** auth-store 的 sessionKind：只有个人账号会话（account）才会被请求位置。 */
  sessionKind: string;
  /** iOS 审核态（演示会话 / 审核构建的离线守卫期）：必须零网络请求。 */
  iosReview: boolean;
  /** 持有 Attendance.Punch.Self（管理员由 hasPermission 自动放行）；没有权限就根本不发请求。 */
  hasPunchSelfPermission: boolean;
  mustChangePassword: boolean;
  userGuid: string | null | undefined;
}

/**
 * 能不能消费位置请求待办：返回当前账号编号，不满足条件返回 null（调用方不发任何请求）。
 * 设备会话 / 设备账号会话是门店共用设备，取到的位置是设备的而不是员工本人的，所以不处理，与推送注册口径一致。
 */
export function resolveLocationRequestUserGuid(snapshot: LocationRequestSessionSnapshot): string | null {
  if (snapshot.iosReview) return null;
  if (!snapshot.isAuthenticated || snapshot.sessionKind !== "account") return null;
  if (!snapshot.hasPunchSelfPermission || snapshot.mustChangePassword) return null;
  const userGuid = snapshot.userGuid?.trim();
  return userGuid ? userGuid : null;
}

// ───────────── 触发来源 ─────────────

/**
 * launch：冷启动就绪 / 登录完成；foreground：App 回到前台；push-received：App 在前台时收到位置请求推送；
 * push-tap：员工点了位置请求推送；inbox-tap：员工在通知收件箱里点了位置请求通知。
 */
export type LocationRequestTrigger = "launch" | "foreground" | "push-received" | "push-tap" | "inbox-tap";

/** 交互模式：员工主动点了通知，才允许走系统定位权限申请；其它触发一律非交互（权限没授予就静默跳过）。 */
export function isInteractiveTrigger(trigger: LocationRequestTrigger): boolean {
  return trigger === "push-tap" || trigger === "inbox-tap";
}

/** 不受 15 秒拉取间隔限制的触发：交互触发，以及「刚收到位置请求推送」（请求很可能就是刚创建的）。 */
export function bypassesFetchThrottle(trigger: LocationRequestTrigger): boolean {
  return trigger === "push-received" || isInteractiveTrigger(trigger);
}

// ───────────── 提示文案选择 ─────────────

/** 轻量提示的内容（文案在 attendance 命名空间的 locationRequest.*）。 */
export type LocationRequestNotice =
  | { kind: "reported-named"; name: string }
  | { kind: "reported-generic" }
  | { kind: "reported-many"; count: number }
  | { kind: "location-unavailable" };

/** 成功上报后的提示：只有一条请求且知道请求人姓名时写明姓名，多条合并成条数，其余用泛称。 */
export function buildReportedNotice(requests: readonly AttendanceLocationRequest[]): LocationRequestNotice | null {
  if (requests.length === 0) return null;
  if (requests.length > 1) return { kind: "reported-many", count: requests.length };
  const name = requests[0].requestedByName?.trim();
  return name ? { kind: "reported-named", name } : { kind: "reported-generic" };
}
