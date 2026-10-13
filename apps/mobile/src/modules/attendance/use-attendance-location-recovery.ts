import { useEffect } from "react";
import { AppState } from "react-native";
import { recoverAttendanceLocationTracking } from "@/modules/attendance/location-tracking";

/**
 * 班中定位的前台恢复：
 * - 挂载（冷启动就绪）时执行一次；
 * - App 每次从后台回到前台时再执行一次。
 * 每次执行 = 自愈后台定位任务（被系统回收则按原选项重启）+ 补传本地队列里尚未上传的点。
 *
 * 挂在根布局而不是 shell 布局：根布局在任何路由（含登录页）下都已挂载，
 * 并且和 TaskManager.defineTask 所在的 location-tracking 模块同属应用启动路径。
 * enabled 由根布局的 sideEffectsEnabled 控制（启动就绪且非 iOS 审核态）；
 * 审核会话的守卫在 recoverAttendanceLocationTracking 内部还有一层。
 */
export function useAttendanceLocationRecovery({ enabled }: { enabled: boolean }) {
  useEffect(() => {
    if (!enabled) {
      return;
    }

    const recover = () => {
      void recoverAttendanceLocationTracking().catch((error: unknown) => {
        console.warn("[attendance-location] 前台恢复班中定位失败", error);
      });
    };

    if (AppState.currentState === "active") {
      recover();
    }
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        recover();
      }
    });
    return () => subscription.remove();
  }, [enabled]);
}
