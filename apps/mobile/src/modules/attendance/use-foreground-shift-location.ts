import { useEffect } from "react";
import { AppState } from "react-native";
import { runForegroundShiftLocationSample } from "@/modules/attendance/foreground-shift-location-runtime";

/**
 * 班中「回前台取一次位置」：
 * - 挂载（冷启动就绪）时若 App 在前台，尝试一次；
 * - 每次从后台回到前台，再尝试一次。
 * 是否真的取位置由 runForegroundShiftLocationSample 判定：只有已上班、未下班、已授权「使用 App 期间」、
 * 距上次尝试满 10 分钟才会取；否则什么都不做。这里不会启动后台定位，也不会弹权限框。
 *
 * 挂在根布局，与旧任务清理 hook 同属 sideEffectsEnabled（启动就绪且非 iOS 审核态）。
 */
export function useForegroundShiftLocationSample({ enabled }: { enabled: boolean }) {
  useEffect(() => {
    if (!enabled) {
      return;
    }

    // runForegroundShiftLocationSample 内部兜住所有错误，不会 reject。
    const run = () => {
      void runForegroundShiftLocationSample();
    };

    if (AppState.currentState === "active") {
      run();
    }
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        run();
      }
    });
    return () => subscription.remove();
  }, [enabled]);
}
