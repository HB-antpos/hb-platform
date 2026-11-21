import { useEffect } from "react";

import {
  isSameBleScannerIndicator,
  toBleScannerIndicatorSnapshot,
} from "./ble-scanner-indicator";
import { usePosShellStore } from "./pos-shell-store";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";

/**
 * 把 BLE 扫码枪的连接状态投影到 UI 壳，供顶栏图标显示。
 * 没有 BLE 服务（旧原生包）时状态保持 null，顶栏不显示该图标。
 */
export function BleScannerStatusBridge() {
  const runtime = usePosRuntime();
  const ble = runtime.services?.scanner.ble ?? null;
  const setBleScanner = usePosShellStore((state) => state.setBleScanner);

  useEffect(() => {
    if (!ble) {
      setBleScanner(null);
      return undefined;
    }

    // 控制器状态频繁推送（RSSI、最近扫码时间），只在顶栏关心的字段变化时才写入 store。
    const publish = (status: Parameters<typeof toBleScannerIndicatorSnapshot>[0]) => {
      const next = toBleScannerIndicatorSnapshot(status);
      const current = usePosShellStore.getState().bleScanner;
      if (!isSameBleScannerIndicator(current, next)) setBleScanner(next);
    };

    // subscribeStatus 注册后不会立即回调：先取一次初值，否则启动时已连上的枪会一直不显示。
    publish(ble.getStatus());
    const unsubscribe = ble.subscribeStatus(publish);
    return () => {
      unsubscribe();
      setBleScanner(null);
    };
  }, [ble, setBleScanner]);

  return null;
}
