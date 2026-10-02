/**
 * 登录诊断：登录各阶段先记在本机（AsyncStorage，不走 Keychain），
 * 下次登录成功后由已登录用户补传到中心日志 HbwebExpo。App 不内置长期日志 Key。
 *
 * 只补传异常：未完成（App 被关闭前停在某一步）、超时、非账号原因的失败、以及耗时过长的成功登录。
 * 正常登录与输错密码不上传，避免日志噪音。
 */

export const LOGIN_DIAGNOSTICS_STORAGE_KEY = "hb.auth.loginDiagnostics.v1";
export const MAX_STORED_LOGIN_TRACES = 5;
export const SLOW_LOGIN_THRESHOLD_MS = 8000;
/** 本机 Keychain/存储读写：正常几十毫秒，超过即视为卡住。 */
export const LOGIN_LOCAL_STEP_TIMEOUT_MS = 8000;
/** 需要联网的步骤：比 axios 30 秒超时多留余量，网络超时仍按原有网络错误提示。 */
export const LOGIN_NETWORK_STEP_TIMEOUT_MS = 35000;
/** 菜单加载自带降级，超时只记录不报错。 */
export const LOGIN_MENU_TIMEOUT_MS = 20000;

export type LoginStage =
  | "start"
  | "localClear"
  | "loginApi"
  | "saveAccessToken"
  | "saveRefreshToken"
  | "currentUser"
  | "saveUser"
  | "sessionMarker"
  | "menu"
  | "done";

export type LoginOutcome = "success" | "failed" | "timeout";

export interface LoginTraceStage {
  stage: LoginStage;
  elapsedMs: number;
  /** 当时 iOS 审核闸门是否关闭（关闭时所有请求会被转到本地审核适配器）。 */
  reviewGateActive: boolean;
}

export interface LoginTrace {
  traceId: string;
  startedAtUtc: string;
  startedAtMs: number;
  username: string;
  appVersion?: string | null;
  appBuildVersion?: string | null;
  runtimeVersion?: string | null;
  updateId?: string | null;
  platform?: string | null;
  stages: LoginTraceStage[];
  outcome?: LoginOutcome;
  /** 仅超时时有值：明确知道卡在哪一步；普通失败看 stages 最后一项即可。 */
  failedStage?: LoginStage;
  errorCode?: string;
  errorMessage?: string;
  httpStatus?: number;
  menuTimedOut?: boolean;
  totalMs?: number;
}

export interface LoginTraceStorage {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
}

export class LoginStepTimeoutError extends Error {
  readonly code = "LOGIN_STEP_TIMEOUT";

  constructor(readonly stage: LoginStage, readonly timeoutMs: number) {
    super(`LOGIN_STEP_TIMEOUT: ${stage} 超过 ${timeoutMs}ms 未完成`);
    this.name = "LoginStepTimeoutError";
  }
}

/** 给登录某一步加超时：超时只让登录流程继续往下走（报错恢复界面），不取消底层原生调用。 */
export function withLoginStepTimeout<T>(
  promise: Promise<T>,
  stage: LoginStage,
  timeoutMs: number,
): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new LoginStepTimeoutError(stage, timeoutMs)), timeoutMs);
  });
  return Promise.race([promise, timeout]).finally(() => {
    if (timer) clearTimeout(timer);
  });
}

export function createLoginTrace(input: {
  traceId: string;
  username: string;
  nowMs: number;
  reviewGateActive: boolean;
  appInfo?: Pick<LoginTrace, "appVersion" | "appBuildVersion" | "runtimeVersion" | "updateId" | "platform">;
}): LoginTrace {
  return {
    traceId: input.traceId,
    startedAtUtc: new Date(input.nowMs).toISOString(),
    startedAtMs: input.nowMs,
    username: input.username.trim(),
    ...input.appInfo,
    stages: [{ stage: "start", elapsedMs: 0, reviewGateActive: input.reviewGateActive }],
  };
}

export function appendLoginStage(
  trace: LoginTrace,
  stage: LoginStage,
  nowMs: number,
  reviewGateActive: boolean,
): LoginTrace {
  return {
    ...trace,
    stages: [...trace.stages, { stage, elapsedMs: nowMs - trace.startedAtMs, reviewGateActive }],
  };
}

function readHttpStatus(error: unknown) {
  const response = (error as { response?: { status?: unknown } } | null)?.response;
  return typeof response?.status === "number" ? response.status : undefined;
}

export function finishLoginTrace(
  trace: LoginTrace,
  outcome: LoginOutcome,
  nowMs: number,
  error?: unknown,
): LoginTrace {
  const finished: LoginTrace = { ...trace, outcome, totalMs: nowMs - trace.startedAtMs };
  if (outcome === "success") return finished;

  const record = (error ?? {}) as { code?: unknown; message?: unknown; stage?: unknown };
  return {
    ...finished,
    failedStage: error instanceof LoginStepTimeoutError ? error.stage : undefined,
    errorCode: typeof record.code === "string" ? record.code : undefined,
    errorMessage: typeof record.message === "string" ? record.message.slice(0, 300) : undefined,
    httpStatus: readHttpStatus(error),
  };
}

/** 账号原因（登录接口返回 4xx，如密码错误、账号停用）属于正常业务失败，不上传。 */
function isCredentialRejection(trace: LoginTrace) {
  return trace.outcome === "failed"
    && !trace.stages.some((stage) => stage.stage === "loginApi")
    && trace.httpStatus !== undefined
    && trace.httpStatus >= 400
    && trace.httpStatus < 500;
}

/**
 * 选出需要补传的记录。没有结果的记录（且不是当前正在进行的那次）说明 App 在登录途中被关闭——正是卡死的场景。
 */
export function selectLoginTracesToUpload(traces: LoginTrace[], currentTraceId?: string) {
  return traces.filter((trace) => {
    if (trace.traceId === currentTraceId) return false;
    if (!trace.outcome) return true;
    if (trace.outcome === "success") return (trace.totalMs ?? 0) >= SLOW_LOGIN_THRESHOLD_MS || Boolean(trace.menuTimedOut);
    return !isCredentialRejection(trace);
  });
}

export function upsertLoginTrace(traces: LoginTrace[], trace: LoginTrace) {
  const others = traces.filter((item) => item.traceId !== trace.traceId);
  // 新记录放最后，只保留最近几条，避免长期不登录时无限增长。
  return [...others, trace].slice(-MAX_STORED_LOGIN_TRACES);
}

export function parseStoredLoginTraces(raw: string | null): LoginTrace[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed)
      ? parsed.filter((item): item is LoginTrace =>
          Boolean(item && typeof item === "object" && typeof (item as LoginTrace).traceId === "string"))
      : [];
  } catch {
    return [];
  }
}

function describeTrace(trace: LoginTrace) {
  const lastStage = trace.stages[trace.stages.length - 1]?.stage ?? "start";
  if (!trace.outcome) return `移动端登录未完成：App 关闭前停在 ${lastStage}`;
  if (trace.outcome === "timeout") return `移动端登录超时：${trace.failedStage ?? lastStage} 未完成`;
  if (trace.outcome === "failed") return `移动端登录失败：${lastStage} 之后出错`;
  return trace.menuTimedOut
    ? `移动端登录成功但菜单加载超时（${trace.totalMs}ms）`
    : `移动端登录耗时过长（${trace.totalMs}ms）`;
}

/** 转成中心日志条目；traceId 作为 ClientEventId，服务端按它幂等去重，重复补传不会写两条。 */
export function buildLoginDiagnosticLogItems(traces: LoginTrace[], environment: string) {
  return traces.map((trace) => ({
    clientEventId: trace.traceId,
    level: trace.outcome === "success" ? "Information" : "Warning",
    message: describeTrace(trace),
    timestampUtc: trace.startedAtUtc,
    environment,
    sourceType: "Mobile",
    serviceName: "HbwebExpoApp",
    category: "auth.login",
    appVersion: [trace.appVersion, trace.appBuildVersion].filter(Boolean).join("+") || undefined,
    properties: {
      traceId: trace.traceId,
      username: trace.username,
      outcome: trace.outcome ?? "abandoned",
      failedStage: trace.failedStage,
      lastStage: trace.stages[trace.stages.length - 1]?.stage,
      totalMs: trace.totalMs,
      errorCode: trace.errorCode,
      errorMessage: trace.errorMessage,
      httpStatus: trace.httpStatus,
      menuTimedOut: trace.menuTimedOut,
      runtimeVersion: trace.runtimeVersion,
      updateId: trace.updateId,
      platform: trace.platform,
      stages: trace.stages,
    },
  }));
}

export type LoginDiagnosticsUploadResult = "skipped" | "uploaded" | "dropped" | "kept";

/**
 * 补传并清理本机记录：成功或服务端明确拒收（未启用/格式不对）都删掉已选记录，网络失败则保留下次再传。
 */
export async function uploadPendingLoginDiagnostics(deps: {
  storage: LoginTraceStorage;
  upload: (items: ReturnType<typeof buildLoginDiagnosticLogItems>) => Promise<void>;
  environment: string;
  currentTraceId?: string;
}): Promise<LoginDiagnosticsUploadResult> {
  const traces = parseStoredLoginTraces(await deps.storage.getString(LOGIN_DIAGNOSTICS_STORAGE_KEY));
  const selected = selectLoginTracesToUpload(traces, deps.currentTraceId);
  // 处理完后只留进行中的那条；正常登录、输错密码这类无需上传的已结束记录也一并清掉。
  const inProgress = traces.filter((trace) => trace.traceId === deps.currentTraceId);
  const saveInProgressOnly = () =>
    deps.storage.setString(LOGIN_DIAGNOSTICS_STORAGE_KEY, JSON.stringify(inProgress));

  if (!selected.length) {
    if (inProgress.length !== traces.length) await saveInProgressOnly();
    return "skipped";
  }

  try {
    await deps.upload(buildLoginDiagnosticLogItems(selected, deps.environment));
  } catch (error) {
    const status = readHttpStatus(error);
    // 网络失败、限流、服务端错误、接口尚未上线（404，OTA 先于后端时）：保留下次再传；
    // 未启用（403）或格式被拒（400）则丢弃，免得反复重传。
    if (status === undefined || status >= 500 || status === 429 || status === 401 || status === 404) {
      return "kept";
    }
    await saveInProgressOnly();
    return "dropped";
  }

  await saveInProgressOnly();
  return "uploaded";
}
