import { useIsFocused } from "@react-navigation/native";
import { usePathname } from "expo-router";
import {
  createContext,
  type PropsWithChildren,
  useContext,
  useEffect,
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
