import { useEffect, useMemo, useRef } from "react";
import { AppState, type AppStateStatus } from "react-native";

import { usePosShellStore } from "./pos-shell-store";
import { RuntimeWorkController } from "./runtime-work-controller";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";

export function RuntimeWorkBridge() {
  const runtime = usePosRuntime();
  const applicationLog = runtime.services?.applicationLog ?? null;
  const connectivity = usePosShellStore((state) => state.connectivity);
  const lastAppState = useRef<AppStateStatus>(AppState.currentState);
  const controller = useMemo(
    () =>
      runtime.services
        ? new RuntimeWorkController(runtime.services)
        : null,
    [runtime.services],
  );

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
