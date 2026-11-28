import { Redirect, router, type Href } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  OrderingWorkbenchScreen,
  signOutOrderingNative,
  useOrderingSessionStore,
  type OrderingSignOutReason,
} from "@/features/ordering";
import { expoOrderingCrypto } from "@/features/ordering/ordering-crypto";
import type { OrderingWebBridge } from "@/features/ordering/ordering-web-bridge";
import { toggleAppLanguage } from "@/i18n";
import { useRouteBleScan } from "@/ui/scanner/scanner-route-bridge";
import { usePosThemeVersion } from "@/ui/theme";

/**
 * 订货工作台：仓库订货（hotbargain.vip/shop）+ 供应商订货助手。
 *
 * 中文注释：
 * - 没有订货登录就回订货登录页；收银员登录状态不影响这里，反过来也一样。
 * - 已注册设备：订货助手默认用本机绑定的分店（账号能访问时）。
 * - 退出：先在网页窗口里作废网页会话（那份 Cookie 最新），网页不在时走原生兜底，再回首页。
 */
export default function OrderingRoute() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const user = useOrderingSessionStore((state) => state.user);
  const clearUser = useOrderingSessionStore((state) => state.signOut);
  const [deviceStoreCode, setDeviceStoreCode] = useState<string | null>(null);
  const scanHandlerRef = useRef<((barcode: string) => void) | null>(null);

  const deviceSession = runtime.services?.deviceSession ?? null;
  useEffect(() => {
    if (!deviceSession) return undefined;
    let cancelled = false;
    void deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (!cancelled) setDeviceStoreCode(identity?.storeCode ?? null);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [deviceSession]);

  useRouteBleScan({
    path: "/ordering",
    enabled: user !== null,
    onScan: (value) => scanHandlerRef.current?.(value),
  });
  const registerScanHandler = useCallback(
    (handler: ((barcode: string) => void) | null) => {
      scanHandlerRef.current = handler;
    },
    [],
  );

  const signOut = useCallback(
    async (_reason: OrderingSignOutReason, bridge: OrderingWebBridge) => {
      const loggedOut = await bridge.logout();
      if (!loggedOut) await signOutOrderingNative();
      router.dismissTo("/" as Href);
      clearUser();
    },
    [clearUser],
  );

  if (!user) return <Redirect href={"/ordering-login" as Href} />;

  return (
    <OrderingWorkbenchScreen
      crypto={expoOrderingCrypto}
      deviceStoreCode={deviceStoreCode}
      language={i18n.resolvedLanguage ?? i18n.language}
      onSwitchLanguage={() => void toggleAppLanguage()}
      registerScanHandler={registerScanHandler}
      signOut={signOut}
      user={user}
    />
  );
}
