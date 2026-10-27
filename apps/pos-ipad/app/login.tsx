import { router, type Href } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { ExpoPosUpdateIdentity } from "@/core/runtime/expo-pos-runtime";
import {
  type NativeAppVersion,
  otaVersionLabel,
  UNKNOWN_NATIVE_APP_VERSION,
} from "@/core/runtime/native-app-version";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  type CashierLoginVersionInfo,
  CashierLoginController,
  CashierLoginScreen,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { toggleAppLanguage } from "@/i18n";
import { RouteHidScannerCapture } from "@/ui/scanner/scanner-route-bridge";

export default function LoginRoute() {
  const runtime = usePosRuntime();
  const versionInfo = toCashierLoginVersionInfo(
    runtime.services?.updateIdentity,
    runtime.services?.nativeAppVersion,
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
  const signInFromScanner = useCallback(
    async (barcode: string) => {
      if (scanInFlight.current) return;
      scanInFlight.current = true;
      try {
        // 与手动登录共用公开 controller，扫描结果不会绕过终端状态与权限校验。
        await new CashierLoginController(useCashierLoginStore.getState()).login(
          barcode,
          runtime,
        );
        router.replace("/sales" as Href);
      } catch {
        // 登录页面保留原状供收银员手动重试；不向路由层暴露认证失败详情。
      } finally {
        scanInFlight.current = false;
      }
    },
    [runtime],
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
        onManualInputFocusChange={handleManualInputFocusChange}
        onOpenNetworkCheck={() => router.push("/network-check" as Href)}
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
  nativeVersion: NativeAppVersion | null | undefined,
): CashierLoginVersionInfo {
  const runtimeVersion = identity?.runtimeVersion.trim() || "unknown";
  return Object.freeze({
    ...(nativeVersion ?? UNKNOWN_NATIVE_APP_VERSION),
    runtimeVersion,
    otaVersion: otaVersionLabel(identity),
  });
}
