import { create } from "zustand";

import type { BleScannerIndicatorSnapshot } from "./ble-scanner-indicator";

import type { DisplayStatus, PrinterStatus } from "@/core/contracts";
import type { ScannerCaptureStatus } from "@/core/peripherals/scanner";

export type ConnectivityStatus = "checking" | "online" | "offline";
export type DeviceGateStatus =
  | "unregistered"
  | "pending-approval"
  | "authorized"
  | "locked";
export type PendingSyncStatus =
  | Readonly<{ kind: "checking" }>
  | Readonly<{ kind: "ready"; count: number }>
  | Readonly<{ kind: "unavailable" }>;

export type TerminalPresentation = Readonly<{
  storeName: string | null;
  deviceCode: string;
}>;

type PosShellState = {
  connectivity: ConnectivityStatus;
  deviceGate: DeviceGateStatus;
  pendingSync: PendingSyncStatus;
  printer: PrinterStatus;
  scanner: ScannerCaptureStatus;
  /** BLE 扫码枪连接状态；null = 本机没有 BLE 服务，顶栏不显示该图标。 */
  bleScanner: BleScannerIndicatorSnapshot | null;
  /** 顶栏点按重连 BLE 扫码枪的入口；null = 没有 BLE 服务。由 BleScannerStatusBridge 注册。 */
  bleScannerReconnect: (() => void) | null;
  /**
   * 顶栏「未配对」点按打开配对面板的入口；null = 当前页面没有配对面板，「未配对」不可点。
   * 目前只有设备注册页登记（新设备进不了登录页 / 设置页，只能在注册页配扫码枪）。
   */
  bleScannerOpenPairing: (() => void) | null;
  display: DisplayStatus;
  terminalPresentation: TerminalPresentation | null;
  setConnectivity(status: ConnectivityStatus): void;
  setDeviceGate(status: DeviceGateStatus): void;
  setPendingSync(status: PendingSyncStatus): void;
  setPrinter(status: PrinterStatus): void;
  setScanner(status: ScannerCaptureStatus): void;
  setBleScanner(status: BleScannerIndicatorSnapshot | null): void;
  setBleScannerReconnect(reconnect: (() => void) | null): void;
  setBleScannerOpenPairing(openPairing: (() => void) | null): void;
  setDisplay(status: DisplayStatus): void;
  setTerminalPresentation(
    terminalPresentation: TerminalPresentation | null,
  ): void;
  reset(): void;
};

const initialStatus = {
  connectivity: "checking",
  deviceGate: "unregistered",
  pendingSync: { kind: "checking" },
  printer: "disconnected",
  scanner: "inactive",
  bleScanner: null,
  bleScannerReconnect: null,
  bleScannerOpenPairing: null,
  display: "disconnected",
  terminalPresentation: null,
} as const;

export const usePosShellStore = create<PosShellState>((set) => ({
  ...initialStatus,
  setConnectivity: (connectivity) => set({ connectivity }),
  setDeviceGate: (deviceGate) => set({ deviceGate }),
  setPendingSync: (pendingSync) => {
    if (
      pendingSync.kind === "ready" &&
      (!Number.isSafeInteger(pendingSync.count) || pendingSync.count < 0)
    ) {
      throw new TypeError("pending sync count must be a non-negative safe integer");
    }
    set({ pendingSync: Object.freeze({ ...pendingSync }) });
  },
  setPrinter: (printer) => set({ printer }),
  setScanner: (scanner) => set({ scanner }),
  setBleScanner: (bleScanner) => set({ bleScanner }),
  setBleScannerReconnect: (bleScannerReconnect) => set({ bleScannerReconnect }),
  setBleScannerOpenPairing: (bleScannerOpenPairing) => set({ bleScannerOpenPairing }),
  setDisplay: (display) => set({ display }),
  setTerminalPresentation: (terminalPresentation) =>
    set({ terminalPresentation }),
  reset: () => set(initialStatus),
}));
