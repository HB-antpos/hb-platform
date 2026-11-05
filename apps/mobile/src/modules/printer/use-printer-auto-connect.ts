import { useEffect, useRef } from "react";
import { AppState, type AppStateStatus } from "react-native";
import {
  connectSavedPrinter,
  hydrateSavedPrinter,
  syncPrinterStatus,
} from "@/modules/printer/api";
import { subscribePrinterStatusChanged } from "@/modules/printer/native";
import { usePrinterStore } from "@/modules/printer/state";
import { i18n } from "@/shared/i18n/i18n";

const RECONNECT_INTERVAL_MS = 5000;
/** 退避后的最长重连间隔。 */
export const MAX_RECONNECT_INTERVAL_MS = 60_000;
/**
 * 连续失败达到该次数后，本轮不再自动重连（约 7 分钟：5+10+20+40 秒，之后每次 60 秒）。
 * 打印机关机或不在范围内时，不能在前台无限次发起蓝牙连接：每次连接最长占用约 15 秒无线电，
 * 长时间重复会持续耗电发热（曾有用户一次前台会话 21 分钟内连续失败 100 次）。
 */
export const MAX_CONSECUTIVE_RECONNECT_FAILURES = 10;

/** 连续失败 n 次后到下一次重连的等待时间：5s、10s、20s、40s，之后封顶 60s。 */
export function getReconnectDelayMs(consecutiveFailures: number) {
  const exponent = Math.max(0, Math.min(Math.trunc(consecutiveFailures), 10));
  return Math.min(RECONNECT_INTERVAL_MS * 2 ** exponent, MAX_RECONNECT_INTERVAL_MS);
}

export function usePrinterAutoConnect(
  { enabled = true }: { enabled?: boolean } = {}
) {
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const autoReconnectPaused = usePrinterStore((state) => state.autoReconnectPaused);
  const hydrated = usePrinterStore((state) => state.hydrated);
  const status = usePrinterStore((state) => state.status);
  const setStatus = usePrinterStore((state) => state.setStatus);
  const setLastError = usePrinterStore((state) => state.setLastError);

  const savedPrinterAddress = savedPrinter?.address;
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const connectInFlightRef = useRef(false);
  const lastConnectAttemptRef = useRef(0);
  // 连续失败次数与「本轮放弃」标记：连上、回到前台、蓝牙重新打开、换打印机或用户在设置里操作后清零。
  const consecutiveFailuresRef = useRef(0);
  const gaveUpRef = useRef(false);
  const bluetoothWasOffRef = useRef(false);

  useEffect(() => {
    if (!enabled) {
      return;
    }
    void hydrateSavedPrinter().catch((error: unknown) => {
      setLastError(error instanceof Error ? error.message : i18n.t("common:errors.requestFailed"));
      setStatus("error");
    });
  }, [enabled, setLastError, setStatus]);

  useEffect(() => {
    if (!enabled || !hydrated) return;
    let cancelled = false;
    appStateRef.current = AppState.currentState;
    // 打印机地址或暂停状态变化会重新进入这里（含用户在设置里手动重连/换机），从头开始计数。
    consecutiveFailuresRef.current = 0;
    gaveUpRef.current = false;

    async function tick() {
      if (cancelled || appStateRef.current !== "active") return;
      try {
        // 原生事件到来时立即同步；连接等待期间也允许刷新状态，避免界面滞后。
        const nativeStatus = await syncPrinterStatus();
        if (cancelled) return;
        const current = usePrinterStore.getState();
        if (current.autoReconnectPaused || !current.savedPrinter) return;
        if (nativeStatus.connected && nativeStatus.address === current.savedPrinter.address) {
          lastConnectAttemptRef.current = 0;
          consecutiveFailuresRef.current = 0;
          gaveUpRef.current = false;
          return;
        }
        if (nativeStatus.supported && !nativeStatus.enabled) bluetoothWasOffRef.current = true;
        if (!nativeStatus.supported || !nativeStatus.enabled || connectInFlightRef.current) return;
        if (bluetoothWasOffRef.current) {
          // 蓝牙从关闭变为打开：用户已采取动作，重新开始自动重连。
          bluetoothWasOffRef.current = false;
          consecutiveFailuresRef.current = 0;
          gaveUpRef.current = false;
        }
        // 已连续失败太多次：本轮不再自动发起，等回到前台 / 蓝牙重开 / 手动连接再恢复。
        if (gaveUpRef.current) return;
        if (Date.now() - lastConnectAttemptRef.current < getReconnectDelayMs(consecutiveFailuresRef.current)) return;

        connectInFlightRef.current = true;
        lastConnectAttemptRef.current = Date.now();
        try {
          await connectSavedPrinter({ status: "reconnecting" });
          consecutiveFailuresRef.current = 0;
        } catch (connectError) {
          consecutiveFailuresRef.current += 1;
          if (consecutiveFailuresRef.current >= MAX_CONSECUTIVE_RECONNECT_FAILURES) gaveUpRef.current = true;
          throw connectError;
        } finally {
          connectInFlightRef.current = false;
        }
      } catch (error) {
        if (!cancelled) {
          const current = usePrinterStore.getState();
          setLastError(error instanceof Error ? error.message : i18n.t("common:errors.requestFailed"));
          setStatus(current.autoReconnectPaused ? "paused" : "error");
        }
      }
    }

    const nativeUnsubscribe = subscribePrinterStatusChanged(() => { void tick(); });
    const appSubscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;
      if (nextState === "active") {
        // 从后台回到前台视为新一轮：清零失败计数，恢复 5 秒的基础重连间隔。
        if (previousState !== "active") {
          consecutiveFailuresRef.current = 0;
          gaveUpRef.current = false;
        }
        void tick();
      }
    });
    const storeUnsubscribe = usePrinterStore.subscribe((current, previous) => {
      // 旧包写失败由 JS 清理连接，也应立即触发恢复；连接失败留给定时重试，避免热循环。
      if (current.status === "disconnected" && previous.status !== "disconnected") void tick();
    });
    const intervalId = setInterval(() => { void tick(); }, RECONNECT_INTERVAL_MS);
    void tick();

    return () => {
      cancelled = true;
      nativeUnsubscribe();
      appSubscription.remove();
      storeUnsubscribe();
      clearInterval(intervalId);
    };
  }, [autoReconnectPaused, enabled, hydrated, savedPrinterAddress, setLastError, setStatus]);

  return {
    status,
    savedPrinter,
    autoReconnectPaused,
  };
}
