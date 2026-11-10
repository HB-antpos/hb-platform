import * as Application from "expo-application";
import { useMemo, type ReactNode } from "react";

import { ZellerTerminalBoundary } from "./zeller-terminal-boundary";

import type { ZellerSdkVendor } from "@/features/zeller-probe/zeller-sdk-host";

/**
 * 「支付设置」里的 Zeller 页签目前只做配对：Zeller 收款还没有接进收银流程。
 * 正式版（production 构建）先不提供，避免门店配好了却发现不能启用；接入收款后再放开。
 */
const ZELLER_SETTINGS_AVAILABLE = process.env.EXPO_PUBLIC_HBPOS_BUILD_PROFILE !== "production";

/**
 * 包在设置页外层：提供了 Zeller 终端，设置页才会出现 Zeller 页签。
 * SDK 要等第一次打开该页签才加载；正式版原样返回 children，什么都不加。
 */
export function ZellerSettingsBoundary({
  onOpenNetworkCheck,
  children,
}: Readonly<{ onOpenNetworkCheck?: () => void; children: ReactNode }>) {
  const vendor = useMemo<ZellerSdkVendor>(
    () => ({
      vendorName: "Hot Bargain",
      vendorApplicationName: "HB POS iPad",
      vendorApplicationVersion: Application.nativeApplicationVersion ?? "0.0.0",
      vendorDeviceType: "tablet",
    }),
    [],
  );
  if (!ZELLER_SETTINGS_AVAILABLE) return <>{children}</>;
  return (
    <ZellerTerminalBoundary
      vendor={vendor}
      {...(onOpenNetworkCheck ? { onOpenNetworkCheck } : {})}
    >
      {children}
    </ZellerTerminalBoundary>
  );
}
