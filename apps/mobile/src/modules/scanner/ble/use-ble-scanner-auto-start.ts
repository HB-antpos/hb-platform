import { useEffect } from "react";
import { AppState } from "react-native";
import {
  resumeBleScannerService,
  startBleScannerService,
  stopBleScannerService,
} from "./ble-scanner-service";

/**
 * 根布局用：App 启动就绪后，如果本机保存过 BLE 扫码枪就自动连接；回到前台时重新挂一次连接请求。
 * enabled 与打印机自动连接同一个开关（sideEffectsEnabled：启动完成且不在 iOS 审核态）。
 */
export function useBleScannerAutoStart({ enabled }: { enabled: boolean }) {
  useEffect(() => {
    if (!enabled) {
      return;
    }
    void startBleScannerService({ onlyIfSaved: true });
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        resumeBleScannerService();
      }
    });
    return () => {
      subscription.remove();
      stopBleScannerService();
    };
  }, [enabled]);
}
