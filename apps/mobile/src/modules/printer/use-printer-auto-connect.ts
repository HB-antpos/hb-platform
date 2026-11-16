import { useEffect, useRef } from "react";
import { AppState, type AppStateStatus } from "react-native";
import {
  connectSavedPrinter,
  hydrateSavedPrinter,
  syncPrinterStatus,
} from "@/modules/printer/api";
import { recordPrinterLink } from "@/modules/printer/link-diagnostics";
import { subscribePrinterStatusChanged } from "@/modules/printer/native";
import {
  IDLE_RECONNECT_INFO,
  usePrinterStore,
  type PrinterReconnectPhase,
} from "@/modules/printer/state";
import { i18n } from "@/shared/i18n/i18n";

const RECONNECT_INTERVAL_MS = 5000;
/** 退避后的最长重连间隔。 */
export const MAX_RECONNECT_INTERVAL_MS = 60_000;
/**
 * 连续失败达到该次数后进入「探测」档（约 7 分钟：5+10+20+40 秒，之后每次 60 秒）。
 * 打印机关机、休眠或不在范围内时，不能在前台高频发起蓝牙连接：每次连接最长占用约 15 秒无线电，
 * 长时间重复会持续耗电发热（曾有用户一次前台会话 21 分钟内连续失败 100 次）。
 * 但也不能就此彻底放弃：门店反馈打印机休眠后恢复，App 却一直不再自动连，只能重启才连得上。
 * 所以进入探测档后继续低频尝试，而不是停止。
 */
export const MAX_CONSECUTIVE_RECONNECT_FAILURES = 10;
/** 探测档的前半段：每分钟一次，共 10 次（约 10 分钟）。 */
export const PROBE_FAST_ATTEMPTS = 10;
export const PROBE_FAST_INTERVAL_MS = 60_000;
/** 探测档的后半段：每 5 分钟一次，直到连上或回到前台 / 手动重试（占空比约 5%）。 */
export const PROBE_SLOW_INTERVAL_MS = 300_000;

function normalizeFailures(consecutiveFailures: number) {
  return Number.isFinite(consecutiveFailures) ? Math.max(0, Math.trunc(consecutiveFailures)) : 0;
}

/**
 * 连续失败 n 次后到下一次重连的等待时间：
 * 5s、10s、20s、40s，之后 60s（前 10 次）；第 10～19 次失败后每 60 秒；第 20 次起每 5 分钟。
 */
export function getReconnectDelayMs(consecutiveFailures: number) {
  const failures = normalizeFailures(consecutiveFailures);
  if (failures >= MAX_CONSECUTIVE_RECONNECT_FAILURES + PROBE_FAST_ATTEMPTS) return PROBE_SLOW_INTERVAL_MS;
  if (failures >= MAX_CONSECUTIVE_RECONNECT_FAILURES) return PROBE_FAST_INTERVAL_MS;
  return Math.min(RECONNECT_INTERVAL_MS * 2 ** Math.min(failures, 10), MAX_RECONNECT_INTERVAL_MS);
}

/** 连续失败 n 次时所处的重连阶段（0 次为 idle）。 */
export function getReconnectPhase(consecutiveFailures: number): PrinterReconnectPhase {
  const failures = normalizeFailures(consecutiveFailures);
  if (failures === 0) return "idle";
  if (failures >= MAX_CONSECUTIVE_RECONNECT_FAILURES + PROBE_FAST_ATTEMPTS) return "probe_slow";
  if (failures >= MAX_CONSECUTIVE_RECONNECT_FAILURES) return "probe_fast";
  return "backoff";
}

function publishReconnectInfo(failures: number) {
  const store = usePrinterStore.getState();
  store.setReconnectInfo(failures === 0 ? IDLE_RECONNECT_INFO : { failures, phase: getReconnectPhase(failures) });
}

export function usePrinterAutoConnect(
  { enabled = true }: { enabled?: boolean } = {}
) {
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const autoReconnectPaused = usePrinterStore((state) => state.autoReconnectPaused);
  const hydrated = usePrinterStore((state) => state.hydrated);
  const status = usePrinterStore((state) => state.status);
  const retryNonce = usePrinterStore((state) => state.retryNonce);
  const setStatus = usePrinterStore((state) => state.setStatus);
  const setLastError = usePrinterStore((state) => state.setLastError);

  const savedPrinterAddress = savedPrinter?.address;
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const connectInFlightRef = useRef(false);
  const lastConnectAttemptRef = useRef(0);
  // 连续失败次数：连上、回到前台、蓝牙重新打开、换打印机或用户点「重新连接」后清零。
  const consecutiveFailuresRef = useRef(0);
  // 已记录过的阶段：只在进入新阶段时写一条诊断，避免每次失败都刷日志。
  const recordedPhaseRef = useRef<PrinterReconnectPhase>("idle");
  const bluetoothWasOffRef = useRef(false);
  const seenRetryNonceRef = useRef(retryNonce);

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

    /** 清零失败计数，重新开始一轮；之前已在失败中时记一条「恢复自动重连」，说明是什么触发的。 */
    function resetRound(reason: "retry_nonce" | "settings" | "foreground" | "bluetooth_on") {
      const previous = consecutiveFailuresRef.current;
      consecutiveFailuresRef.current = 0;
      recordedPhaseRef.current = "idle";
      publishReconnectInfo(0);
      if (previous > 0) {
        recordPrinterLink("auto.reconnect", { phase: "resumed", failures: previous, resumeReason: reason });
      }
    }

    // 打印机地址、暂停状态或「立即重试」信号变化会重新进入这里，从头开始计数。
    const nonceChanged = seenRetryNonceRef.current !== retryNonce;
    seenRetryNonceRef.current = retryNonce;
    resetRound(nonceChanged ? "retry_nonce" : "settings");
    // 手动重连刚结束（无论成败）：下一次自动尝试至少等一个基础间隔，不能紧接着再连一次。
    if (nonceChanged) lastConnectAttemptRef.current = Date.now();

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
          recordedPhaseRef.current = "idle";
          publishReconnectInfo(0);
          return;
        }
        if (nativeStatus.supported && !nativeStatus.enabled) bluetoothWasOffRef.current = true;
        if (!nativeStatus.supported || !nativeStatus.enabled || connectInFlightRef.current) return;
        if (bluetoothWasOffRef.current) {
          // 蓝牙从关闭变为打开：用户已采取动作，重新开始自动重连。
          bluetoothWasOffRef.current = false;
          resetRound("bluetooth_on");
        }
        // 失败越多，间隔越长：前 10 次 5~60 秒，之后每分钟、再之后每 5 分钟探测一次，永不彻底放弃。
        if (Date.now() - lastConnectAttemptRef.current < getReconnectDelayMs(consecutiveFailuresRef.current)) return;

        connectInFlightRef.current = true;
        lastConnectAttemptRef.current = Date.now();
        try {
          await connectSavedPrinter({ status: "reconnecting" });
          consecutiveFailuresRef.current = 0;
          recordedPhaseRef.current = "idle";
          publishReconnectInfo(0);
        } catch (connectError) {
          const failures = consecutiveFailuresRef.current + 1;
          consecutiveFailuresRef.current = failures;
          publishReconnectInfo(failures);
          const phase = getReconnectPhase(failures);
          // 进入探测档就是原来「连续失败 10 次后停止自动重连」的那一刻：现在不再停止，明确记一条。
          if ((phase === "probe_fast" || phase === "probe_slow") && recordedPhaseRef.current !== phase) {
            recordedPhaseRef.current = phase;
            recordPrinterLink("auto.reconnect", { phase, failures, nextInMs: getReconnectDelayMs(failures) });
          }
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
          resetRound("foreground");
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
  }, [autoReconnectPaused, enabled, hydrated, retryNonce, savedPrinterAddress, setLastError, setStatus]);

  return {
    status,
    savedPrinter,
    autoReconnectPaused,
  };
}
