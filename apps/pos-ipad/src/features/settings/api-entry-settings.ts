/**
 * 设置页「收银入口（线路）」的视图模型（手持 / iPad 各自一份相同实现，界面文案与样式留在各自设置页）。
 *
 * 数据来自共享协调器（@hb/pos-api-client 的 ApiEntrySettingsController）：
 * 当前实际在用的线路、设置来源（手动 / 后台指定 / 默认）、手动覆盖。
 */
import { useEffect, useState } from "react";

import type {
  ApiEntrySettingsController,
  ApiEntrySettingsSnapshot,
} from "@hb/pos-api-client/api-entry-mode-coordinator";
/**
 * 实际使用的线路会因故障转移而变化，而选择器不发通知：设置页打开期间按这个间隔刷新一次快照。
 * 只刷新卡片自己的状态，不会重绘整个设置页。
 */
export const API_ENTRY_SNAPSHOT_REFRESH_MS = 3000;

function sameSnapshot(
  a: ApiEntrySettingsSnapshot | null,
  b: ApiEntrySettingsSnapshot | null,
): boolean {
  if (a === null || b === null) return a === b;
  return (
    a.manualMode === b.manualMode &&
    a.backendMode === b.backendMode &&
    a.preferredMode === b.preferredMode &&
    a.activeMode === b.activeMode &&
    a.usingFallback === b.usingFallback &&
    a.source === b.source &&
    a.manualSaveFailed === b.manualSaveFailed
  );
}

/** 订阅协调器并定时刷新；没有传入控制器（运行时未接线）时返回 null，界面据此隐藏整张卡片。 */
export function useApiEntrySettings(
  controller: ApiEntrySettingsController | undefined,
): ApiEntrySettingsSnapshot | null {
  const [snapshot, setSnapshot] = useState<ApiEntrySettingsSnapshot | null>(
    () => controller?.getSnapshot() ?? null,
  );
  useEffect(() => {
    if (!controller) {
      setSnapshot(null);
      return undefined;
    }
    const refresh = () => {
      const next = controller.getSnapshot();
      setSnapshot((current) => (sameSnapshot(current, next) ? current : next));
    };
    refresh();
    const unsubscribe = controller.subscribe(refresh);
    const timer = setInterval(refresh, API_ENTRY_SNAPSHOT_REFRESH_MS);
    return () => {
      unsubscribe();
      clearInterval(timer);
    };
  }, [controller]);
  return snapshot;
}
