import { Redirect, type Href, useRouter } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { SupplierAssistantScreen } from "@/features/supplier-assistant/supplier-assistant-screen";
import type { SupplierAssistantTokenSource } from "@/features/supplier-assistant/supplier-assistant-session";
import { useRouteBleScan } from "@/ui/scanner/scanner-route-bridge";
import { PosRouteLoadingScreen } from "@/ui/screens/pos-route-loading-screen";
import { usePosThemeVersion } from "@/ui/theme";

/**
 * 订货助手：设备绑定哪家分店，就用那家分店的订货助手功能，不需要登录 HB 网站账号。
 * 和其他页面一样先过设备身份 + 收银员门禁；进入后分店、令牌都由设备身份决定。
 */
export default function SupplierAssistantRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore((state) => state.clearActiveCashier);
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const [tokenSource, setTokenSource] = useState<SupplierAssistantTokenSource | null>(null);
  const scanHandlerRef = useRef<((barcode: string) => void) | null>(null);

  useEffect(() => {
    if (gate !== "check-device-identity" || !activeCashier || !runtime.services) {
      setTokenSource(null);
      return undefined;
    }
    let cancelled = false;
    const services = runtime.services;
    void services.deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (cancelled) return;
        if (!identity || !isActiveCashierBoundToDevice(activeCashier, identity)) {
          clearActiveCashier();
          return;
        }
        setTokenSource(services.supplierAssistant ?? null);
      })
      .catch(() => {
        if (!cancelled) clearActiveCashier();
      });
    return () => {
      cancelled = true;
    };
  }, [activeCashier, clearActiveCashier, gate, runtime.services]);

  // BLE 扫码枪只收不抢焦点；键盘模式扫码枪的按键直接进网页，由插件脚本识别。
  useRouteBleScan({
    path: "/supplier-assistant",
    enabled: tokenSource !== null,
    onScan: (value) => scanHandlerRef.current?.(value),
  });
  const registerScanHandler = useCallback((handler: ((barcode: string) => void) | null) => {
    scanHandlerRef.current = handler;
  }, []);

  if (gate === "redirect-index") return <Redirect href={"/" as Href} />;
  if (gate === "redirect-login") return <Redirect href={"/login" as Href} />;
  if (!tokenSource) return <PosRouteLoadingScreen active="supplier-assistant" />;

  return (
    <SupplierAssistantScreen
      onBack={() => router.dismissTo("/sales" as Href)}
      registerScanHandler={registerScanHandler}
      tokenSource={tokenSource}
    />
  );
}
