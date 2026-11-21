import { useIsFocused } from "@react-navigation/native";
import { usePathname } from "expo-router";
import {
  createContext,
  type PropsWithChildren,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";

import {
  HidScannerCapture,
  type HidScannerRouter,
  type RoutedScanEvent,
  type ScannerCaptureContext,
} from "@/core/peripherals/scanner";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { usePosThemeVersion } from "@/ui/theme";

type ScannerRouteContextValue = Readonly<{
  scanner: HidScannerRouter | null;
  supervisorAwaiting: boolean;
}>;

type RouteHidScannerCaptureProps = Readonly<{
  context: ScannerCaptureContext;
  enabled?: boolean;
  onHidTextChange?: () => void;
  onScan(
    value: string,
    source?: RoutedScanEvent["source"],
  ): Promise<void> | void;
  path: string;
}>;

const ScannerRouteContext = createContext<ScannerRouteContextValue>({
  scanner: null,
  supervisorAwaiting: false,
});

/**
 * 根路由只管理扫码 context；主管授权弹窗仍由 AppProviders 挂载，避免复制授权 UI。
 */
export function ScannerRouteProvider({ children }: PropsWithChildren) {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const scanner = runtime.services?.scanner.router ?? null;
  const authorization = runtime.services?.operationAuthorization;
  const [supervisorAwaiting, setSupervisorAwaiting] = useState(false);

  useEffect(() => {
    if (!authorization || authorization.status !== "available") {
      setSupervisorAwaiting(false);
      return undefined;
    }
    // 授权状态是公开 facade；路由层不接触主管票据或认证实现。
    const sync = () => {
      setSupervisorAwaiting(
        authorization.getState().kind === "awaiting-supervisor",
      );
    };
    sync();
    return authorization.subscribe(sync);
  }, [authorization]);

  useEffect(() => {
    if (!scanner || !supervisorAwaiting) return undefined;
    return scanner.acquireContext("supervisor-authorization");
  }, [scanner, supervisorAwaiting]);

  return (
    <ScannerRouteContext.Provider value={{ scanner, supervisorAwaiting }}>
      {children}
    </ScannerRouteContext.Provider>
  );
}

/** 仅在当前可见路由内捕获 HID，路由失焦、主管弹窗和卸载都会释放焦点与 context。 */
export function RouteHidScannerCapture(props: RouteHidScannerCaptureProps) {
  usePosThemeVersion();
  const { scanner, supervisorAwaiting } = useContext(ScannerRouteContext);
  if (!scanner) return null;
  return (
    <ActiveRouteHidScannerCapture
      {...props}
      scanner={scanner}
      supervisorAwaiting={supervisorAwaiting}
    />
  );
}

function ActiveRouteHidScannerCapture({
  context,
  enabled = true,
  onHidTextChange,
  onScan,
  path,
  scanner,
  supervisorAwaiting,
}: RouteHidScannerCaptureProps &
  Readonly<{
    scanner: HidScannerRouter;
    supervisorAwaiting: boolean;
  }>) {
  usePosThemeVersion();
  const isFocused = useIsFocused();
  const pathname = usePathname();
  const active =
    enabled && isFocused && pathname === path && !supervisorAwaiting;

  useEffect(() => {
    if (!active) return undefined;
    const releaseContext = scanner.acquireContext(context);
    const unsubscribe = scanner.subscribeRouted((event) => {
      if (event.context !== context) return;
      void onScan(event.value, event.source);
    });
    return () => {
      unsubscribe();
      releaseContext();
    };
  }, [active, context, onScan, scanner]);

  return (
    <HidScannerCapture
      active={active}
      focusRequestKey={`${path}:${supervisorAwaiting ? "supervisor" : "route"}`}
      {...(onHidTextChange ? { onHidTextChange } : {})}
      scanner={scanner}
    />
  );
}

type RouteBleScanOptions = Readonly<{
  /** 为 false 时不申请场景租约（例如页面内有弹窗要独占输入）。 */
  enabled?: boolean;
  /** 租约对应的扫码场景，默认 product。 */
  context?: ScannerCaptureContext;
  /** 收到 BLE 扫码枪的整码（已去换行、去重、拦截开通码）。 */
  onScan(value: string): void;
  path: string;
}>;

/**
 * 让「有查码输入框」的页面接入 BLE 扫码枪：页面聚焦时申请场景租约（BLE 码只投递给持有租约的页面），
 * 并只订阅 BLE 来源的码。
 *
 * 与 RouteHidScannerCapture 的区别：不挂隐藏输入框、不抢焦点——键盘模式（HID）扫码枪继续像原来一样
 * 往当前聚焦的输入框打字，手输和 HID 都不受影响；只有 BLE 数据模式的枪（不经过键盘）靠这个 hook 收码。
 * 主管授权弹窗出现时自动让出租约，由授权弹窗独占扫码。
 */
export function useRouteBleScan({
  context = "product",
  enabled = true,
  onScan,
  path,
}: RouteBleScanOptions): void {
  const { scanner, supervisorAwaiting } = useContext(ScannerRouteContext);
  const isFocused = useIsFocused();
  const pathname = usePathname();
  const active =
    enabled && isFocused && pathname === path && !supervisorAwaiting;
  // 回调常为内联函数；放进 ref，避免每次渲染都释放 / 重新申请租约（会清空路由器的半段缓冲）。
  const onScanRef = useRef(onScan);
  onScanRef.current = onScan;

  useEffect(() => {
    if (!scanner || !active) return undefined;
    const releaseContext = scanner.acquireContext(context);
    const unsubscribe = scanner.subscribeRouted((event) => {
      if (event.context !== context || event.source !== "ble") return;
      onScanRef.current(event.value);
    });
    return () => {
      unsubscribe();
      releaseContext();
    };
  }, [active, context, scanner]);
}
