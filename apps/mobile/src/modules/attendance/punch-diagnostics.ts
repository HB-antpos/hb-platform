/**
 * 扫码打卡诊断：打卡失败（或后台定位权限出现异常）时，在本机暂存一条诊断日志，
 * 由已登录用户经 /mobile/diagnostics/logs 补传到中心日志 HbwebExpo。App 不内置长期日志 Key。
 *
 * 为什么要有它：扫码打卡的失败此前完全不留痕——后端把业务失败当 HTTP 200 返回、客户端只弹提示，
 * 11-19 iOS 员工「系统设置已是 Always，打卡页仍提示需要后台定位」就无从查起。
 * 这里记录失败发生在哪个阶段、系统给出的前后台定位权限原始状态、定位/网络校验结果，
 * 严禁记录二维码 token、打卡授权凭证、请求体与经纬度。
 *
 * 本模块不依赖 react-native / expo / 网络 / 存储，纯逻辑，可直接用 tsx 做单测。
 */

/** 扫码打卡链路上的阶段，与 AttendanceScreen.handleAttendanceQrScan 的步骤一一对应。 */
export type PunchDiagnosticStage =
  | "format"
  | "resolve"
  | "store"
  | "today"
  | "permission"
  | "locating"
  | "punch";

/** granted：权限最终放行（只用于 permission 阶段，记 Information）；其余都记 Warning。 */
export type PunchDiagnosticOutcome = "failed" | "denied" | "cancelled" | "granted";

type Scalar = string | number | boolean;

/** 系统返回的一次权限读数（前台或后台）。 */
export interface PunchPermissionReading {
  status?: string;
  granted?: boolean;
  canAskAgain?: boolean;
  /** iOS：whenInUse / always / none。判断「设置里是 Always 但 App 认为没授权」的关键字段。 */
  scope?: string;
  /** Android：fine / coarse / none。 */
  accuracy?: string;
  /** 读取本身抛错时的简短说明。 */
  error?: string;
}

export interface PunchPermissionSnapshot {
  foreground: PunchPermissionReading;
  background: PunchPermissionReading;
  /** 系统定位总开关。 */
  servicesEnabled?: boolean;
}

export interface PunchVerificationSummary {
  locationStatus?: string;
  locationReason?: string;
  locationPermission?: string;
  networkStatus?: string;
  networkVerification?: string;
}

export interface PunchDiagnosticEvent {
  stage: PunchDiagnosticStage;
  outcome: PunchDiagnosticOutcome;
  /** 阶段内的具体原因，如 backgroundRequired / gpsRequired / requestDenied。 */
  reason?: string;
  /** 后端或客户端的业务错误码。 */
  errorCode?: string;
  httpStatus?: number;
  errorMessage?: string;
  punchType?: string;
  /** 员工是否在本次扫码流程里点过「继续」（定位说明弹层）。 */
  consentedBeforeScan?: boolean;
  /** 本次扫码从开始到出事的耗时。 */
  elapsedMs?: number;
  /** 出事前后的权限读数：before 是判定时，after 是申请系统权限之后。 */
  permissionBefore?: PunchPermissionSnapshot;
  permissionAfter?: PunchPermissionSnapshot;
  verification?: PunchVerificationSummary;
}

export interface PunchDiagnosticContext {
  environment: string;
  appVersion?: string;
  /** 平台、系统版本、运行时、更新 ID 等设备环境，原样并入 properties。 */
  properties?: Record<string, Scalar | null | undefined>;
}

export interface PunchDiagnosticLogItem {
  /** 入队时固定，服务端按它幂等去重，补传重试不会写两条。 */
  clientEventId: string;
  level: "Warning" | "Information";
  message: string;
  timestampUtc: string;
  environment: string;
  sourceType: "Mobile";
  serviceName: "HbwebExpoApp";
  category: "attendance.punch";
  appVersion?: string;
  properties: Record<string, unknown>;
}

export interface PunchDiagnosticStorage {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
}

export const PUNCH_DIAGNOSTICS_STORAGE_KEY = "hb.attendance.punchDiagnostics.v1";
/** 与后端单次上限（20 条）对齐；本机暂存超过时丢弃最旧的。 */
export const MAX_PENDING_PUNCH_LOGS = 20;
export const PUNCH_TEXT_MAX = 160;

const STAGE_LABELS: Record<PunchDiagnosticStage, string> = {
  format: "二维码格式校验",
  resolve: "二维码解析",
  store: "门店匹配",
  today: "读取今日状态",
  permission: "后台定位权限",
  locating: "定位准备",
  punch: "提交打卡",
};

const OUTCOME_LABELS: Record<PunchDiagnosticOutcome, string> = {
  failed: "失败",
  denied: "被拒绝",
  cancelled: "已取消",
  granted: "已放行",
};

/** 截断并压成单行，避免异常信息里的换行、超长内容进入日志。 */
export function clipDiagnosticText(value: unknown, max = PUNCH_TEXT_MAX): string | undefined {
  if (typeof value !== "string") return undefined;
  const text = value.replace(/\s+/g, " ").trim();
  if (!text) return undefined;
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

/**
 * 把 expo-location 的权限响应压成一次读数。
 * 只取状态类字段，不依赖 expo 类型（本模块不引入 expo），未知结构一律当空读数。
 */
export function summarizePermissionResponse(raw: unknown): PunchPermissionReading {
  if (!raw || typeof raw !== "object") return {};
  const response = raw as {
    status?: unknown;
    granted?: unknown;
    canAskAgain?: unknown;
    ios?: { scope?: unknown } | null;
    android?: { accuracy?: unknown } | null;
  };
  const reading: PunchPermissionReading = {};
  if (typeof response.status === "string") reading.status = response.status;
  if (typeof response.granted === "boolean") reading.granted = response.granted;
  if (typeof response.canAskAgain === "boolean") reading.canAskAgain = response.canAskAgain;
  if (typeof response.ios?.scope === "string") reading.scope = response.ios.scope;
  if (typeof response.android?.accuracy === "string") reading.accuracy = response.android.accuracy;
  return reading;
}

export function readHttpStatus(error: unknown): number | undefined {
  const response = (error as { response?: { status?: unknown } } | null)?.response;
  return typeof response?.status === "number" ? response.status : undefined;
}

function putReading(target: Record<string, unknown>, prefix: string, reading: PunchPermissionReading) {
  const entries: [string, Scalar | undefined][] = [
    ["Status", reading.status],
    ["Granted", reading.granted],
    ["CanAskAgain", reading.canAskAgain],
    ["Scope", reading.scope],
    ["Accuracy", reading.accuracy],
    ["Error", clipDiagnosticText(reading.error)],
  ];
  for (const [suffix, value] of entries) {
    if (value !== undefined) target[`${prefix}${suffix}`] = value;
  }
}

function putSnapshot(
  target: Record<string, unknown>,
  prefix: "before" | "after",
  snapshot: PunchPermissionSnapshot | undefined,
) {
  if (!snapshot) return;
  putReading(target, `${prefix}Fg`, snapshot.foreground);
  putReading(target, `${prefix}Bg`, snapshot.background);
  if (snapshot.servicesEnabled !== undefined) target[`${prefix}ServicesEnabled`] = snapshot.servicesEnabled;
}

/**
 * 生成一条诊断日志。
 * 属性名全部是纯字母数字驼峰且避开后端的敏感片段（pin / pan / card / token / header 等），
 * 否则值会被服务端替换成 [REDACTED]，见 punch-diagnostics.test.ts 里的守卫。
 */
export function buildPunchDiagnosticLog(input: {
  event: PunchDiagnosticEvent;
  nowMs: number;
  id: string;
  context: PunchDiagnosticContext;
}): PunchDiagnosticLogItem {
  const { event, nowMs, id, context } = input;
  const properties: Record<string, unknown> = {
    stage: event.stage,
    outcome: event.outcome,
  };
  const put = (key: string, value: Scalar | undefined) => {
    if (value !== undefined) properties[key] = value;
  };
  put("reason", clipDiagnosticText(event.reason, 80));
  put("errorCode", clipDiagnosticText(event.errorCode, 80));
  put("httpStatus", event.httpStatus);
  put("errorMessage", clipDiagnosticText(event.errorMessage));
  put("punchType", clipDiagnosticText(event.punchType, 20));
  put("consentedBeforeScan", event.consentedBeforeScan);
  put("elapsedMs", event.elapsedMs === undefined ? undefined : Math.max(0, Math.round(event.elapsedMs)));
  putSnapshot(properties, "before", event.permissionBefore);
  putSnapshot(properties, "after", event.permissionAfter);
  if (event.verification) {
    put("verifyLocationStatus", clipDiagnosticText(event.verification.locationStatus, 40));
    put("verifyLocationReason", clipDiagnosticText(event.verification.locationReason, 80));
    put("verifyLocationPermission", clipDiagnosticText(event.verification.locationPermission, 40));
    put("verifyNetworkStatus", clipDiagnosticText(event.verification.networkStatus, 40));
    put("verifyNetworkMode", clipDiagnosticText(event.verification.networkVerification, 40));
  }
  for (const [key, value] of Object.entries(context.properties ?? {})) {
    if (value !== undefined && value !== null && !(key in properties)) properties[key] = value;
  }

  const detail = clipDiagnosticText(event.reason ?? event.errorCode, 60);
  const message = `扫码打卡${STAGE_LABELS[event.stage]}${OUTCOME_LABELS[event.outcome]}${detail ? `：${detail}` : ""}`;

  return {
    clientEventId: id,
    level: event.outcome === "granted" ? "Information" : "Warning",
    message,
    timestampUtc: new Date(nowMs).toISOString(),
    environment: context.environment,
    sourceType: "Mobile",
    serviceName: "HbwebExpoApp",
    category: "attendance.punch",
    ...(context.appVersion ? { appVersion: context.appVersion } : {}),
    properties,
  };
}

export function parsePendingPunchLogs(raw: string | null): PunchDiagnosticLogItem[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed)
      ? parsed.filter((item): item is PunchDiagnosticLogItem =>
          Boolean(item && typeof item === "object" && typeof (item as PunchDiagnosticLogItem).clientEventId === "string"))
      : [];
  } catch {
    return [];
  }
}

export function appendPendingPunchLog(items: PunchDiagnosticLogItem[], item: PunchDiagnosticLogItem) {
  // 新日志放最后；超过后端单次上限时丢最旧的，长期离线也不会无限增长。
  return [...items.filter((existing) => existing.clientEventId !== item.clientEventId), item]
    .slice(-MAX_PENDING_PUNCH_LOGS);
}

export type PunchDiagnosticUploadResult = "skipped" | "uploaded" | "dropped" | "kept";

/**
 * 补传本机暂存的打卡诊断：成功或服务端明确拒收（未启用 403 / 格式不对 400）都清掉，
 * 网络失败、限流、服务端错误、未登录、接口尚未上线（OTA 先于后端）则保留下次再传。
 */
export async function uploadPendingPunchLogs(deps: {
  storage: PunchDiagnosticStorage;
  upload: (items: PunchDiagnosticLogItem[]) => Promise<void>;
}): Promise<PunchDiagnosticUploadResult> {
  const pending = parsePendingPunchLogs(await deps.storage.getString(PUNCH_DIAGNOSTICS_STORAGE_KEY));
  if (!pending.length) return "skipped";

  const batch = pending.slice(0, MAX_PENDING_PUNCH_LOGS);
  const remaining = () => {
    const sent = new Set(batch.map((item) => item.clientEventId));
    return pending.filter((item) => !sent.has(item.clientEventId));
  };
  try {
    await deps.upload(batch);
  } catch (error) {
    const status = readHttpStatus(error);
    if (status === undefined || status >= 500 || status === 429 || status === 401 || status === 404) {
      return "kept";
    }
    await deps.storage.setString(PUNCH_DIAGNOSTICS_STORAGE_KEY, JSON.stringify(remaining()));
    return "dropped";
  }
  await deps.storage.setString(PUNCH_DIAGNOSTICS_STORAGE_KEY, JSON.stringify(remaining()));
  return "uploaded";
}
