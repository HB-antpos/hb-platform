import { useEffect } from "react";
import { AppState } from "react-native";
import {
  pauseBleScannerService,
  resumeBleScannerService,
  startBleScannerService,
  stopBleScannerService,
} from "./ble-scanner-service";

/**
 * 根布局用：App 启动就绪后，如果本机保存过 BLE 扫码枪就自动连接。
 * 回到前台时休眠 / 重连中的扫码枪立即补一轮唤醒扫描；进后台（含 inactive）停止扫描。
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
      } else {
        pauseBleScannerService();
      }
    });
    return () => {
      subscription.remove();
      stopBleScannerService();
    };
  }, [enabled]);
}
