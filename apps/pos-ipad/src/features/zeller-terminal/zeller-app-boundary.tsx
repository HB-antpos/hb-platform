import * as Application from "expo-application";
import { type Href, useRouter } from "expo-router";
import { useEffect, useMemo, useSyncExternalStore, type ReactNode } from "react";

import { isZellerSettingsAvailable } from "./zeller-availability";
import { useZellerHideSdkUiControl } from "./zeller-hide-sdk-ui";
import { ZellerTerminalBoundary, useZellerTerminalBoundary } from "./zeller-terminal-boundary";

import { zellerTerminalRegistry } from "@/features/payments/zeller/zeller-terminal-registry";
import type { ZellerSdkVendor } from "@/features/zeller-probe/zeller-sdk-host";

/**
 * 设置页的 Zeller 页签（配对、启用）目前只在非正式构建里开放：Zeller 收款还没有经过真机和刷卡机验证。
 * 正式版（production 构建）不显示页签，也就没有办法启用 Zeller，SDK 永远不会被加载。
 * 例外：EAS 档位 `production-zeller` 会带 `EXPO_PUBLIC_HBPOS_ENABLE_ZELLER=1`，
 * 让测试员能在 TestFlight 包里验证 Zeller（规则见 zeller-availability.ts）。
 */
const ZELLER_SETTINGS_AVAILABLE = isZellerSettingsAvailable({
  buildProfile: process.env.EXPO_PUBLIC_HBPOS_BUILD_PROFILE,
  enableZeller: process.env.EXPO_PUBLIC_HBPOS_ENABLE_ZELLER,
});

/**
 * 包在整个 App 的根上：全 App 只有这一处 Zeller SDK。
 *
 * 中文注释：
 * 1. 必须在根上。SDK 的登录窗口和刷卡窗口是它自己渲染的全屏弹层，设置页配对和收银页刷卡都要用；
 *    而且全 App 只能有一个 SDK 实例，两个实例会各开一个 WebView，互相抢同一台刷卡机。
 * 2. 默认不加载。SDK 只在两种情况下加载：有人打开了设置里的 Zeller 页签，或运行时提出需要 Zeller 终端
 *    （设置里启用了 Zeller、或有 Zeller 的付款要核实）。没用到 Zeller 的门店完全不受影响。
 * 3. SDK 就绪后把终端登记给运行时，收款适配器才发得出请求；卸载时注销。
 */
export function ZellerAppBoundary({ children }: Readonly<{ children: ReactNode }>) {
  const router = useRouter();
  const vendor = useMemo<ZellerSdkVendor>(
    () => ({
      vendorName: "Hot Bargain",
      vendorApplicationName: "HB POS iPad",
      vendorApplicationVersion: Application.nativeApplicationVersion ?? "0.0.0",
      vendorDeviceType: "tablet",
    }),
    [],
  );
  const openNetworkCheck = useMemo(() => () => router.push("/network-check" as Href), [router]);
  // 「收款时不弹出 Zeller 窗口」：本机设置，随根组件一起读取并同步给运行时。
  const hideSdkUi = useZellerHideSdkUiControl();
  return (
    <ZellerTerminalBoundary
      hideSdkUi={hideSdkUi}
      onOpenNetworkCheck={openNetworkCheck}
      settingsTabAvailable={ZELLER_SETTINGS_AVAILABLE}
      vendor={vendor}
    >
      <ZellerTerminalRegistrar />
      {children}
    </ZellerTerminalBoundary>
  );
}

/** 运行时提出需要终端时加载 SDK；SDK 就绪后把终端登记给运行时。不渲染任何界面。 */
export function ZellerTerminalRegistrar() {
  const boundary = useZellerTerminalBoundary();
  const wanted = useSyncExternalStore(
    zellerTerminalRegistry.subscribe,
    zellerTerminalRegistry.isWanted,
    zellerTerminalRegistry.isWanted,
  );
  const activate = boundary?.activate;
  useEffect(() => {
    if (wanted) activate?.();
  }, [activate, wanted]);

  const terminal = boundary?.state.status === "ready" ? boundary.state.terminal : null;
  useEffect(
    () => (terminal ? zellerTerminalRegistry.register(terminal) : undefined),
    [terminal],
  );
  return null;
}
