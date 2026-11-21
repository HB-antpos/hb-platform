import { create } from "zustand";
import type { BleScannerStatus } from "./ble-scanner-controller";

/** 控制器未启动前（或旧原生包 / 审核态）的状态：未配对、无任何设备信息。 */
export const INITIAL_BLE_SCANNER_STATUS: BleScannerStatus = Object.freeze({
  state: "not-paired",
  saved: null,
  batteryLevel: null,
  rssi: null,
  lastScanAt: null,
  lastScanValue: null,
  attention: false,
  lastErrorCode: null,
});

export type BleScannerNotice = Readonly<{
  id: number;
  kind: "no-receiver";
}>;

interface BleScannerState {
  status: BleScannerStatus;
  /** 扫码页面的轻提示（例如当前页面不接收扫码），由 BleScannerNoticeHost 展示。 */
  notice: BleScannerNotice | null;
  setStatus: (status: BleScannerStatus) => void;
  showNotice: (kind: BleScannerNotice["kind"]) => void;
  clearNotice: () => void;
}

// 与打印机状态（src/modules/printer/state.ts）同一写法：控制器是唯一写入方，界面只读。
export const useBleScannerStore = create<BleScannerState>((set) => ({
  status: INITIAL_BLE_SCANNER_STATUS,
  notice: null,
  setStatus: (status) => set({ status }),
  showNotice: (kind) => set((state) => ({ notice: { id: (state.notice?.id ?? 0) + 1, kind } })),
  clearNotice: () => set({ notice: null }),
}));
