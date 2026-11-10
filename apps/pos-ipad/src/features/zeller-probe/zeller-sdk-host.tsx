import {
  useEffect,
  useState,
  type ComponentType,
  type ReactNode,
} from "react";

import type { ZellerProbeTerminal } from "./zeller-probe-runner";
import { usePosThemeVersion } from "@/ui/theme";

/**
 * 把 Zeller React Native SDK 接进页面：SDK 只提供 React 组件和 hook
 * （Provider 包住页面并自己渲染 WebView 弹层，useTerminal() 取客户端），
 * 这里用一个桥接组件把客户端递给页面内容。
 *
 * 中文注释：SDK 用动态 import 懒加载，只在打开验证页时才求值。原因：SDK 入口有全局副作用
 * （import "react-native-url-polyfill/auto" 会替换全局 URL），还会在加载时触碰 5 个原生库；
 * 懒加载保证不打开这个页面的收银流程完全不受影响，原生库缺失时也只是这一页报错。
 */

export type ZellerSdkVendor = Readonly<{
  vendorName: string;
  vendorApplicationName: string;
  vendorApplicationVersion: string;
  vendorDeviceType: string;
}>;

export type ZellerSdkHostState =
  | Readonly<{ status: "loading" }>
  | Readonly<{ status: "failed"; reason: string }>
  | Readonly<{ status: "ready"; terminal: ZellerProbeTerminal }>;

type ZellerSdkModule = Readonly<{
  Provider: ComponentType<ZellerSdkVendor & { children?: ReactNode }>;
  useTerminal: () => unknown;
}>;

type LoadState =
  | Readonly<{ status: "loading" }>
  | Readonly<{ status: "failed"; reason: string }>
  | Readonly<{ status: "ready"; sdk: ZellerSdkModule }>;

export type ZellerSdkHostProps = Readonly<{
  vendor: ZellerSdkVendor;
  children: (state: ZellerSdkHostState) => ReactNode;
}>;

export function ZellerSdkHost({ vendor, children }: ZellerSdkHostProps) {
  usePosThemeVersion();
  const [load, setLoad] = useState<LoadState>({ status: "loading" });

  useEffect(() => {
    let active = true;
    void import("@zeller-public/payments-sdk-react-native")
      .then((module) => {
        if (!active) return;
        const sdk = module as unknown as Partial<ZellerSdkModule>;
        if (typeof sdk.Provider !== "function" || typeof sdk.useTerminal !== "function") {
          setLoad({ status: "failed", reason: "unexpected SDK exports" });
          return;
        }
        setLoad({ status: "ready", sdk: sdk as ZellerSdkModule });
      })
      .catch((error: unknown) => {
        if (!active) return;
        setLoad({
          status: "failed",
          reason: error instanceof Error ? error.message : String(error),
        });
      });
    return () => {
      active = false;
    };
  }, []);

  if (load.status !== "ready") return <>{children(load)}</>;

  const { Provider, useTerminal } = load.sdk;
  return (
    <Provider {...vendor}>
      <TerminalBridge useTerminal={useTerminal}>{children}</TerminalBridge>
    </Provider>
  );
}

/** useTerminal() 必须在 Provider 之内调用；这里取到客户端后交给页面内容。 */
function TerminalBridge({
  useTerminal,
  children,
}: Readonly<{
  useTerminal: () => unknown;
  children: (state: ZellerSdkHostState) => ReactNode;
}>) {
  usePosThemeVersion();
  // SDK 的客户端在结构上满足验证页用到的方法；真实返回值都按 unknown 交给转换层检查。
  const terminal = useTerminal() as ZellerProbeTerminal;
  return <>{children({ status: "ready", terminal })}</>;
}
