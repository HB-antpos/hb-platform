import { useEffect } from "react";
import { AppState } from "react-native";
import { retireLegacyAttendanceLocationTracking } from "@/modules/attendance/location-tracking";

/**
 * 旧版班中后台定位的前台清理：
 * - 挂载（冷启动就绪）时执行一次；
 * - App 每次从后台回到前台时再执行一次。
 * 每次执行 = 若发现旧版本留下的后台定位任务 / 班次上下文 / 待传队列就收掉，没有就什么都不做。
 * 班中后台定位本身已取消，这里不会再启动任何定位。
 *
 * 挂在根布局而不是 shell 布局：根布局在任何路由（含登录页）下都已挂载，
 * 并且和 TaskManager.defineTask 所在的 location-tracking 模块同属应用启动路径。
 * enabled 由根布局的 sideEffectsEnabled 控制（启动就绪且非 iOS 审核态）；
 * 审核会话的守卫在 retireLegacyAttendanceLocationTracking 内部还有一层。
 */
export function useLegacyLocationTrackingCleanup({ enabled }: { enabled: boolean }) {
  useEffect(() => {
    if (!enabled) {
      return;
    }

    // retireLegacyAttendanceLocationTracking 内部兜住所有错误，不会 reject。
    const cleanup = () => {
      void retireLegacyAttendanceLocationTracking();
    };

    if (AppState.currentState === "active") {
      cleanup();
    }
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        cleanup();
      }
    });
    return () => subscription.remove();
  }, [enabled]);
}
