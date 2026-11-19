import Constants from "expo-constants";
import * as Crypto from "expo-crypto";
import * as Location from "expo-location";
import { Platform } from "react-native";
import {
  appendPendingPunchLog,
  buildPunchDiagnosticLog,
  parsePendingPunchLogs,
  PUNCH_DIAGNOSTICS_STORAGE_KEY,
  summarizePermissionResponse,
  uploadPendingPunchLogs,
  type PunchDiagnosticContext,
  type PunchDiagnosticEvent,
  type PunchDiagnosticUploadResult,
  type PunchPermissionReading,
  type PunchPermissionSnapshot,
} from "./punch-diagnostics";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import { apiClient } from "@/shared/api/client";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { useAuthStore } from "@/store/auth-store";

// 本机暂存的读改写全部串行，避免入队与补传清理互相覆盖。
let storageQueue: Promise<unknown> = Promise.resolve();

function serialize<T>(task: () => Promise<T>): Promise<T> {
  const result = storageQueue.then(task, task);
  storageQueue = result.catch(() => undefined);
  return result;
}

function createId() {
  try {
    return Crypto.randomUUID();
  } catch {
    // ClientEventId 在服务端是 GUID 列，兜底也要生成合法的 UUID 形状。
    const hex = (length: number) =>
      Array.from({ length }, () => Math.floor(Math.random() * 16).toString(16)).join("");
    return `${hex(8)}-${hex(4)}-4${hex(3)}-a${hex(3)}-${hex(12)}`;
  }
}

function readEnvironment() {
  const logCenter = (Constants.expoConfig?.extra as { logCenter?: { environment?: unknown } } | undefined)?.logCenter;
  return typeof logCenter?.environment === "string" && logCenter.environment.trim()
    ? logCenter.environment.trim()
    : "production";
}

function readContext(): PunchDiagnosticContext {
  const environment = readEnvironment();
  const base = { platform: Platform.OS, osVersion: String(Platform.Version) };
  try {
    const info = getCurrentAppUpdateInfo();
    return {
      environment,
      appVersion: [info.appVersion, info.appBuildVersion].filter(Boolean).join("+") || undefined,
      properties: {
        ...base,
        runtimeVersion: info.runtimeVersion,
        updateId: info.updateId,
        channel: info.channel,
      },
    };
  } catch {
    return { environment, properties: base };
  }
}

async function readPermission(read: () => Promise<unknown>): Promise<PunchPermissionReading> {
  try {
    return summarizePermissionResponse(await read());
  } catch (error) {
    return { error: error instanceof Error ? error.message : "read failed" };
  }
}

/**
 * 读一次系统给出的前后台定位权限原始状态。只读、不弹窗、永不抛错。
 * 诊断关键：iOS 设置里是 Always 而 App 仍判未授权时，看这里 expo 实际返回的 status / scope。
 */
export async function readLocationPermissionSnapshot(): Promise<PunchPermissionSnapshot> {
  const [foreground, background, servicesEnabled] = await Promise.all([
    readPermission(() => Location.getForegroundPermissionsAsync()),
    readPermission(() => Location.getBackgroundPermissionsAsync()),
    Location.hasServicesEnabledAsync().catch(() => undefined),
  ]);
  return { foreground, background, servicesEnabled };
}

/** 补传本机暂存的打卡诊断；未登录、审核会话不上传，失败静默，下次再试。 */
export function flushPunchDiagnostics(): Promise<PunchDiagnosticUploadResult> {
  // 审核会话的请求会被转到本地适配器，也不允许向外发送日志。
  if (isIosReviewSessionActive()) return Promise.resolve("skipped");
  // 未登录时不发请求，避免触发 401 会话恢复流程；之后打开打卡页会再补传。
  if (!useAuthStore.getState().isAuthenticated) return Promise.resolve("skipped");
  return serialize(() =>
    uploadPendingPunchLogs({
      storage: AppAsyncStorage,
      upload: async (logs) => {
        await apiClient.post("/mobile/diagnostics/logs", { logs });
      },
    }),
  ).catch((): PunchDiagnosticUploadResult => "kept");
}

/**
 * 记录一次扫码打卡诊断事件。先落盘再补传：App 随后被关闭或网络不通，下次打开打卡页仍会补传。
 * 绝不抛错、绝不阻塞打卡流程，调用方不需要 await。
 */
export function reportPunchDiagnostic(event: PunchDiagnosticEvent): void {
  try {
    if (isIosReviewSessionActive()) return;
    const item = buildPunchDiagnosticLog({
      event,
      nowMs: Date.now(),
      id: createId(),
      context: readContext(),
    });
    void serialize(async () => {
      const pending = parsePendingPunchLogs(await AppAsyncStorage.getString(PUNCH_DIAGNOSTICS_STORAGE_KEY));
      await AppAsyncStorage.setString(
        PUNCH_DIAGNOSTICS_STORAGE_KEY,
        JSON.stringify(appendPendingPunchLog(pending, item)),
      );
    })
      .then(() => flushPunchDiagnostics())
      .catch(() => undefined);
  } catch {
    // 诊断本身出问题不能影响打卡。
  }
}
