import Constants from "expo-constants";
import * as Crypto from "expo-crypto";
import { Platform } from "react-native";
import {
  appendLoginStage,
  createLoginTrace,
  finishLoginTrace,
  LOGIN_DIAGNOSTICS_STORAGE_KEY,
  parseStoredLoginTraces,
  uploadPendingLoginDiagnostics,
  upsertLoginTrace,
  type LoginDiagnosticsUploadResult,
  type LoginOutcome,
  type LoginStage,
  type LoginTrace,
} from "./login-diagnostics";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import { apiClient } from "@/shared/api/client";
import { AppAsyncStorage } from "@/shared/storage/async-storage";

export interface LoginTraceRecorder {
  readonly traceId: string;
  mark(stage: LoginStage): void;
  markMenuTimedOut(): void;
  finish(outcome: LoginOutcome, error?: unknown): void;
}

// 本机记录的读改写全部串行，避免登录阶段落盘与补传清理互相覆盖。
let storageQueue: Promise<unknown> = Promise.resolve();
let currentTraceId: string | undefined;

function serialize<T>(task: () => Promise<T>): Promise<T> {
  const result = storageQueue.then(task, task);
  storageQueue = result.catch(() => undefined);
  return result;
}

function createTraceId() {
  try {
    return Crypto.randomUUID();
  } catch {
    return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}`;
  }
}

function readAppInfo(): Pick<LoginTrace, "appVersion" | "appBuildVersion" | "runtimeVersion" | "updateId" | "platform"> {
  try {
    const info = getCurrentAppUpdateInfo();
    return {
      appVersion: info.appVersion,
      appBuildVersion: info.appBuildVersion,
      runtimeVersion: info.runtimeVersion,
      updateId: info.updateId,
      platform: Platform.OS,
    };
  } catch {
    return { platform: Platform.OS };
  }
}

function readEnvironment() {
  const logCenter = (Constants.expoConfig?.extra as { logCenter?: { environment?: unknown } } | undefined)?.logCenter;
  return typeof logCenter?.environment === "string" && logCenter.environment.trim()
    ? logCenter.environment.trim()
    : "production";
}

function persistTrace(trace: LoginTrace) {
  // 落盘只做记录、绝不阻塞登录：存储异常直接忽略。
  void serialize(async () => {
    const traces = parseStoredLoginTraces(await AppAsyncStorage.getString(LOGIN_DIAGNOSTICS_STORAGE_KEY));
    await AppAsyncStorage.setString(
      LOGIN_DIAGNOSTICS_STORAGE_KEY,
      JSON.stringify(upsertLoginTrace(traces, trace)),
    );
  }).catch(() => undefined);
}

/** 开始记录一次账号登录；每完成一步立即落盘，App 中途被关闭也能知道停在哪一步。 */
export function startLoginTrace(username: string): LoginTraceRecorder {
  let trace = createLoginTrace({
    traceId: createTraceId(),
    username,
    nowMs: Date.now(),
    reviewGateActive: isIosReviewSessionActive(),
    appInfo: readAppInfo(),
  });
  currentTraceId = trace.traceId;
  persistTrace(trace);

  return {
    traceId: trace.traceId,
    mark(stage) {
      trace = appendLoginStage(trace, stage, Date.now(), isIosReviewSessionActive());
      persistTrace(trace);
    },
    markMenuTimedOut() {
      trace = { ...trace, menuTimedOut: true };
    },
    finish(outcome, error) {
      trace = finishLoginTrace(trace, outcome, Date.now(), error);
      if (currentTraceId === trace.traceId) currentTraceId = undefined;
      persistTrace(trace);
    },
  };
}

/** 已登录后补传本机暂存的异常登录记录；失败静默，下次登录再试。 */
export function flushLoginDiagnostics(): Promise<LoginDiagnosticsUploadResult> {
  // 审核会话的请求会被转到本地适配器，也不允许向外发送日志。
  if (isIosReviewSessionActive()) return Promise.resolve("skipped");
  return serialize(() =>
    uploadPendingLoginDiagnostics({
      storage: AppAsyncStorage,
      upload: async (logs) => {
        await apiClient.post("/mobile/diagnostics/logs", { logs });
      },
      environment: readEnvironment(),
      currentTraceId,
    }),
  ).catch((): LoginDiagnosticsUploadResult => "kept");
}
