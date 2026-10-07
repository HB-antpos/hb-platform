import { useEffect, useMemo, useRef } from "react";
import { AppState, type AppStateStatus } from "react-native";

import { usePosShellStore } from "./pos-shell-store";
import {
  RuntimeWorkController,
  type RuntimeBackgroundWorkPort,
} from "./runtime-work-controller";

import type { ExpoPosRuntimeServices } from "@/core/runtime/expo-pos-runtime";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";

/** 总部下发小票资料的定时检查周期。 */
const RECEIPT_PROFILE_SYNC_INTERVAL_MS = 60_000;

function isDeviceAuthorized(
  services: Pick<ExpoPosRuntimeServices, "device"> | null,
): boolean {
  return (
    services?.device === "authorized-online" ||
    services?.device === "authorized-local"
  );
}

/** 设备未认证（待注册/待审批/已锁定）时不暴露下发资料同步，避免无凭据请求。 */
function toWorkPort(services: ExpoPosRuntimeServices): RuntimeBackgroundWorkPort {
  return isDeviceAuthorized(services)
    ? services
    : { ...services, receiptProfileSync: undefined };
}

export function RuntimeWorkBridge() {
  const runtime = usePosRuntime();
  const applicationLog = runtime.services?.applicationLog ?? null;
  const connectivity = usePosShellStore((state) => state.connectivity);
  const lastAppState = useRef<AppStateStatus>(AppState.currentState);
  const controller = useMemo(
    () =>
      runtime.services
        ? new RuntimeWorkController(toWorkPort(runtime.services))
        : null,
    [runtime.services],
  );
  // 控制器只在设备认证就绪后才带上下发资料同步入口；设备状态变化会换新 services，
  // 随之重建控制器并立即补一次同步，即「设备认证就绪后立即一次」。
  const receiptProfileEnabled = controller !== null && isDeviceAuthorized(runtime.services);

  useEffect(() => {
    if (!controller) return undefined;
    applicationLog?.onApplicationStarted();
    void controller.onApplicationStarted().catch((error: unknown) => {
      applicationLog?.record({
        level: "Error",
        message: "Runtime background work failed during application startup.",
        category: "runtime.background-work",
        error,
        properties: { trigger: "application-started" },
      });
      // 同步/外设队列保留了失败事实；后台触发器不能让 React 树崩溃。
    });
    return undefined;
  }, [applicationLog, controller]);

  useEffect(() => {
    const updates = runtime.services?.appUpdates;
    if (!updates || connectivity !== "online") return undefined;
    // 与移动端一致，常驻前台的收银设备每 30 分钟补查；只准备下载，不自动安装。
    const timer = setInterval(() => {
      if (AppState.currentState !== "active") return;
      void updates.refreshOnForeground().catch(() => {
        // 保留已有更新策略，下载失败由更新状态条提供重试入口。
      });
    }, 30 * 60 * 1_000);
    return () => clearInterval(timer);
  }, [connectivity, runtime.services]);

  useEffect(() => {
    // 前台常驻且在线时每 60 秒检查一次总部下发的小票资料；后台不轮询，回前台另有立即触发。
    if (!controller || !receiptProfileEnabled || connectivity !== "online") {
      return undefined;
    }
    const timer = setInterval(() => {
      if (AppState.currentState !== "active") return;
      controller.onReceiptProfileTimer();
    }, RECEIPT_PROFILE_SYNC_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [connectivity, controller, receiptProfileEnabled]);

  useEffect(() => {
    if (!controller || connectivity === "checking") return undefined;
    applicationLog?.onNetworkChanged(connectivity === "online");
    void controller
      .onNetworkChanged(connectivity === "online")
      .catch((error: unknown) => {
        applicationLog?.record({
          level: "Error",
          message: "Runtime background work failed after network change.",
          category: "runtime.background-work",
          error,
          properties: { trigger: "network-change" },
        });
        // 网络恢复失败由 outbox 退避，不能在 UI 生命周期中强制重试。
      });
    return undefined;
  }, [applicationLog, connectivity, controller]);

  useEffect(() => {
    if (!controller) return undefined;
    const subscription = AppState.addEventListener("change", (next) => {
      const becameActive =
        next === "active" && lastAppState.current !== "active";
      lastAppState.current = next;
      if (becameActive) {
        applicationLog?.onForeground();
        void controller.onForeground().catch((error: unknown) => {
          applicationLog?.record({
            level: "Error",
            message: "Runtime background work failed after foreground resume.",
            category: "runtime.background-work",
            error,
            properties: { trigger: "foreground" },
          });
          // 前台恢复失败保持队列现状，主管可从同步/履约历史继续处理。
        });
      }
    });
    return () => subscription.remove();
  }, [applicationLog, controller]);

  return null;
}
