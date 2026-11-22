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
  const setBleScannerReconnect = usePosShellStore((state) => state.setBleScannerReconnect);

  useEffect(() => {
    if (!ble) {
      setBleScanner(null);
      setBleScannerReconnect(null);
      return undefined;
    }

    // 顶栏点按重连：控制器会先取消旧的挂起请求再直连，并立即推送「连接中」作为反馈；
    // 失败只体现在状态上（回到休眠 / 重连中），这里吞掉 rejection 避免未处理的 Promise。
    setBleScannerReconnect(() => {
      void ble.reconnect().catch(() => undefined);
    });

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
      setBleScannerReconnect(null);
    };
  }, [ble, setBleScanner, setBleScannerReconnect]);

  return null;
}
