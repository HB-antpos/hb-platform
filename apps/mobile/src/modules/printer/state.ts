import { create } from "zustand";
import type { SavedPrinter } from "@/modules/printer/types";

export type PrinterConnectionState =
  | "idle"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected"
  | "paused"
  | "error";

/**
 * 自动重连所处的阶段（只用于界面提示与诊断）：
 * - idle：没有在失败重试；
 * - backoff：前 10 次失败，间隔 5/10/20/40/60 秒递增；
 * - probe_fast：第 10～19 次失败，每分钟探测一次；
 * - probe_slow：之后每 5 分钟探测一次，不再放弃。
 */
export type PrinterReconnectPhase = "idle" | "backoff" | "probe_fast" | "probe_slow";

export interface PrinterReconnectInfo {
  /** 连续失败次数（连上、回前台、手动重试后清零）。 */
  failures: number;
  phase: PrinterReconnectPhase;
}

export const IDLE_RECONNECT_INFO: PrinterReconnectInfo = { failures: 0, phase: "idle" };

interface PrinterState {
  savedPrinter: SavedPrinter | null;
  status: PrinterConnectionState;
  autoReconnectPaused: boolean;
  lastError: string | null;
  hydrated: boolean;
  /** 「立即重试」信号：每次手动重新连接结束后加一，自动重连 hook 据此清零失败计数、重新开始一轮。 */
  retryNonce: number;
  reconnectInfo: PrinterReconnectInfo;
  setSavedPrinter: (printer: SavedPrinter | null) => void;
  setStatus: (status: PrinterConnectionState) => void;
  setAutoReconnectPaused: (paused: boolean) => void;
  setLastError: (message: string | null) => void;
  setHydrated: (hydrated: boolean) => void;
  bumpRetryNonce: () => void;
  setReconnectInfo: (info: PrinterReconnectInfo) => void;
}

export const usePrinterStore = create<PrinterState>((set) => ({
  savedPrinter: null,
  status: "idle",
  autoReconnectPaused: false,
  lastError: null,
  hydrated: false,
  retryNonce: 0,
  reconnectInfo: IDLE_RECONNECT_INFO,
  setSavedPrinter: (savedPrinter) => set({ savedPrinter }),
  setStatus: (status) => set({ status }),
  setAutoReconnectPaused: (autoReconnectPaused) => set({ autoReconnectPaused }),
  setLastError: (lastError) => set({ lastError }),
  setHydrated: (hydrated) => set({ hydrated }),
  bumpRetryNonce: () => set((state) => ({ retryNonce: state.retryNonce + 1 })),
  setReconnectInfo: (reconnectInfo) =>
    set((state) =>
      state.reconnectInfo.failures === reconnectInfo.failures && state.reconnectInfo.phase === reconnectInfo.phase
        ? state
        : { reconnectInfo },
    ),
}));

// 小票打印机不参与标签打印自动重连，单独维护状态，避免影响商品查询和标签打印链路。
export const useReceiptPrinterStore = create<PrinterState>((set) => ({
  savedPrinter: null,
  status: "idle",
  autoReconnectPaused: false,
  lastError: null,
  hydrated: false,
  retryNonce: 0,
  reconnectInfo: IDLE_RECONNECT_INFO,
  setSavedPrinter: (savedPrinter) => set({ savedPrinter }),
  setStatus: (status) => set({ status }),
  setAutoReconnectPaused: (autoReconnectPaused) => set({ autoReconnectPaused }),
  setLastError: (lastError) => set({ lastError }),
  setHydrated: (hydrated) => set({ hydrated }),
  bumpRetryNonce: () => set((state) => ({ retryNonce: state.retryNonce + 1 })),
  setReconnectInfo: (reconnectInfo) =>
    set((state) =>
      state.reconnectInfo.failures === reconnectInfo.failures && state.reconnectInfo.phase === reconnectInfo.phase
        ? state
        : { reconnectInfo },
    ),
}));
