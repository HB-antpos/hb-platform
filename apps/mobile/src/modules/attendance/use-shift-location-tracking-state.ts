import { useEffect, useState } from "react";
import { AppState } from "react-native";
import { readAttendanceLocationTrackingActive } from "@/modules/attendance/location-tracking";
import type { ShiftLocationTrackingState } from "@/modules/attendance/location-notice-state";

/**
 * 班中定位的真实状态，供打卡页说明条使用。
 * 以下时机重新读取：挂载、refreshKey 变化（今日数据刷新 / 上下班后定位生命周期完成）、App 回到前台。
 * enabled=false（历史日期等）时不读取，直接 unknown，省掉无谓的原生调用。
 */
export function useShiftLocationTrackingState(
  refreshKey: string | number,
  enabled: boolean,
): ShiftLocationTrackingState {
  const [state, setState] = useState<ShiftLocationTrackingState>("unknown");

  useEffect(() => {
    if (!enabled) {
      setState("unknown");
      return undefined;
    }
    let cancelled = false;
    const refresh = () => {
      void readAttendanceLocationTrackingActive().then((active) => {
        if (!cancelled) setState(active ? "active" : "inactive");
      });
    };
    refresh();
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") refresh();
    });
    return () => {
      cancelled = true;
      subscription.remove();
    };
  }, [enabled, refreshKey]);

  return state;
}
