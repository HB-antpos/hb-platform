import { router, type Href } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { ExpoPosUpdateIdentity } from "@/core/runtime/expo-pos-runtime";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  buildCashierLoginDiagnostic,
  type CashierLoginDiagnostic,
  type CashierLoginVersionInfo,
  CashierLoginController,
  CashierLoginScreen,
  toCashierLoginLogDraft,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { toggleAppLanguage } from "@/i18n";
import { RouteHidScannerCapture } from "@/ui/scanner/scanner-route-bridge";

export default function LoginRoute() {
  const runtime = usePosRuntime();
  const versionInfo = toCashierLoginVersionInfo(
    runtime.services?.updateIdentity,
  );
  const { i18n } = useTranslation();
  const scanInFlight = useRef(false);
  // 可见条码框会 autoFocus，首帧先禁用隐藏输入，避免两者竞争焦点。
  const [manualInputActive, setManualInputActive] = useState(true);
  const hidRestoreTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);
  const clearHidRestoreTimer = useCallback(() => {
    if (hidRestoreTimer.current === null) return;
    clearTimeout(hidRestoreTimer.current);
    hidRestoreTimer.current = null;
  }, []);
  const handleManualInputFocusChange = useCallback(
    (focused: boolean) => {
      clearHidRestoreTimer();
      if (!mountedRef.current) return;
      if (focused) {
        setManualInputActive(true);
        return;
      }
      // blur → focus 交接期间继续暂停隐藏输入，下一轮仍未聚焦才恢复 HID。
      hidRestoreTimer.current = setTimeout(() => {
        hidRestoreTimer.current = null;
        if (mountedRef.current) setManualInputActive(false);
      }, 0);
    },
    [clearHidRestoreTimer],
  );
  const applicationLog = runtime.services?.applicationLog ?? null;
  // 扫码登录诊断写进中心日志（Warning，脱敏，不含条码内容），用于排查「条码无效」类登录失败。
  const recordLoginDiagnostic = useCallback(
    (diagnostic: CashierLoginDiagnostic) => {
      applicationLog?.record(toCashierLoginLogDraft(diagnostic));
    },
    [applicationLog],
  );
  // 只在内存里留上一枪的值做截断 / 拼接比对，离开登录页即丢弃，不落盘、不上传。
  const previousScannerBarcode = useRef<string | null>(null);
  const signInFromScanner = useCallback(
    async (barcode: string) => {
      if (scanInFlight.current) return;
      scanInFlight.current = true;
      const previousSubmitted = previousScannerBarcode.current;
      previousScannerBarcode.current = barcode.trim();
      try {
        // 与手动登录共用公开 controller，扫描结果不会绕过终端状态与权限校验。
        await new CashierLoginController(useCashierLoginStore.getState()).login(
          barcode,
          runtime,
        );
        router.replace("/sales" as Href);
      } catch (error: unknown) {
        // 登录页面保留原状供收银员手动重试；不向路由层暴露认证失败详情，只写脱敏诊断。
        recordLoginDiagnostic(
          buildCashierLoginDiagnostic({
            outcome: "rejected",
            trigger: "hid-capture",
            submitted: barcode,
            previousSubmitted,
            error,
            trace: null,
          }),
        );
      } finally {
        scanInFlight.current = false;
      }
    },
    [recordLoginDiagnostic, runtime],
  );
  const handleSwitchLanguage = useCallback(() => {
    void toggleAppLanguage();
  }, []);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      clearHidRestoreTimer();
    };
  }, [clearHidRestoreTimer]);

  return (
    <>
      <RouteHidScannerCapture
        context="cashier-login"
        enabled={!manualInputActive}
        onScan={signInFromScanner}
        path="/login"
      />
      <CashierLoginScreen
        language={i18n.resolvedLanguage ?? i18n.language}
        onLoginDiagnostic={recordLoginDiagnostic}
        onManualInputFocusChange={handleManualInputFocusChange}
        onSwitchLanguage={handleSwitchLanguage}
        onSuccess={() => router.replace("/sales" as Href)}
        // 登录屏幕只得到受限 runtime facade；不从路由注入门店、设备或授权票据。
        runtime={runtime}
        versionInfo={versionInfo}
      />
    </>
  );
}

function toCashierLoginVersionInfo(
  identity: ExpoPosUpdateIdentity | null | undefined,
): CashierLoginVersionInfo {
  const runtimeVersion = identity?.runtimeVersion.trim() || "unknown";
  const updateId = identity?.updateId?.trim().toLowerCase();
  return Object.freeze({
    runtimeVersion,
    otaVersion:
      identity?.isEmbeddedLaunch === false && updateId
        ? updateId.slice(0, 8)
        : "embedded",
  });
}
