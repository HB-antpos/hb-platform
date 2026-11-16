import Constants from "expo-constants";
import * as Crypto from "expo-crypto";
import { AppState, Platform } from "react-native";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import {
  appendPendingLinkLog,
  buildAppStartFields,
  EMPTY_LINK_LAST_SEEN,
  installPrinterLinkRecorder,
  LINK_DIAGNOSTICS_STORAGE_KEY,
  LINK_LAST_SEEN_STORAGE_KEY,
  nextLinkLastSeen,
  parseLinkLastSeen,
  parsePendingLinkLogs,
  recordPrinterLink,
  uploadPendingPrinterLinkLogs,
  type LinkLastSeen,
  type PrinterLinkContext,
  type PrinterLinkLogItem,
  type PrinterLinkUploadResult,
} from "@/modules/printer/link-diagnostics";
import { usePrinterStore } from "@/modules/printer/state";
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

function readContext(): PrinterLinkContext {
  const environment = readEnvironment();
  try {
    const info = getCurrentAppUpdateInfo();
    return {
      environment,
      appVersion: [info.appVersion, info.appBuildVersion].filter(Boolean).join("+") || undefined,
      properties: {
        runtimeVersion: info.runtimeVersion,
        updateId: info.updateId,
        channel: info.channel,
        platform: Platform.OS,
        osVersion: String(Platform.Version),
      },
    };
  } catch {
    return { environment, properties: { platform: Platform.OS } };
  }
}

/** 补传本机暂存的链路日志；未登录、审核会话不上传，失败静默，下次再试。 */
export function flushPrinterLinkDiagnostics(): Promise<PrinterLinkUploadResult> {
  // 审核会话的请求会被转到本地适配器，也不允许向外发送日志。
  if (isIosReviewSessionActive()) return Promise.resolve("skipped");
  // 未登录时不发请求，避免触发 401 会话恢复流程；登录成功后会再补传。
  if (!useAuthStore.getState().isAuthenticated) return Promise.resolve("skipped");
  return serialize(() =>
    uploadPendingPrinterLinkLogs({
      storage: AppAsyncStorage,
      upload: async (logs) => {
        await apiClient.post("/mobile/diagnostics/logs", { logs });
      },
    }),
  ).catch((): PrinterLinkUploadResult => "kept");
}

function enqueue(item: PrinterLinkLogItem) {
  // 先落盘再补传：App 随后被关闭或网络不通，下次启动/回前台仍会补传。
  void serialize(async () => {
    const pending = parsePendingLinkLogs(await AppAsyncStorage.getString(LINK_DIAGNOSTICS_STORAGE_KEY));
    await AppAsyncStorage.setString(
      LINK_DIAGNOSTICS_STORAGE_KEY,
      JSON.stringify(appendPendingLinkLog(pending, item)),
    );
  })
    .then(() => flushPrinterLinkDiagnostics())
    .catch(() => undefined);
}

/**
 * 安装标签打印机蓝牙链路诊断；返回卸载函数。
 * 须在 usePrinterAutoConnect 之前安装，这样挂载时第一次状态同步的事件也能被记录。
 */
export function installPrinterLinkDiagnostics(): () => void {
  installPrinterLinkRecorder({ now: Date.now, newId: createId, emit: enqueue, context: readContext });

  // 冷启动标记：读出上一次进程退出前最后的已知状态，记一条 app.start，
  // 之后看日志就能分清「运行中断线」与「App 被系统杀掉 / 重启后一上来就没连上」。
  const installedAtMs = Date.now();
  let lastSeen: LinkLastSeen = EMPTY_LINK_LAST_SEEN;
  const persistLastSeen = () => {
    lastSeen = nextLinkLastSeen(lastSeen, usePrinterStore.getState().status === "connected", Date.now());
    void AppAsyncStorage.setString(LINK_LAST_SEEN_STORAGE_KEY, JSON.stringify(lastSeen)).catch(() => undefined);
  };
  void AppAsyncStorage.getString(LINK_LAST_SEEN_STORAGE_KEY)
    .then((raw) => {
      const stored = parseLinkLastSeen(raw);
      // 读取期间若已落过盘（lastSeenAtMs 有值），以内存里更新的为准。
      if (stored && lastSeen.lastSeenAtMs === null) lastSeen = stored;
      recordPrinterLink("app.start", buildAppStartFields(stored, installedAtMs), installedAtMs);
    })
    .catch(() => recordPrinterLink("app.start", buildAppStartFields(null, installedAtMs), installedAtMs));

  // 自动重连只在前台运行：前后台切换是“为什么没在重连”的关键线索。
  const appSubscription = AppState.addEventListener("change", (state) => {
    recordPrinterLink("app.state", { state });
    persistLastSeen();
    if (state === "active") void flushPrinterLinkDiagnostics();
  });
  // 用户暂停/恢复自动重连（手动断开、小票测试临时占用）：暂停期间的断线不算故障。
  const unsubscribePrinterStore = usePrinterStore.subscribe((current, previous) => {
    if (current.autoReconnectPaused !== previous.autoReconnectPaused) {
      recordPrinterLink("auto.reconnect", { paused: current.autoReconnectPaused });
    }
    // 连接状态在「已连接」与其他状态之间切换时落盘一次：写入次数 = 连上 / 断开的次数，很少。
    if ((current.status === "connected") !== (previous.status === "connected")) persistLastSeen();
  });
  // 登录成功后补传此前（未登录时）遗留的日志。
  const unsubscribeAuth = useAuthStore.subscribe((current, previous) => {
    if (current.isAuthenticated && !previous.isAuthenticated) void flushPrinterLinkDiagnostics();
  });
  void flushPrinterLinkDiagnostics();

  return () => {
    appSubscription.remove();
    unsubscribePrinterStore();
    unsubscribeAuth();
    installPrinterLinkRecorder(null);
  };
}
