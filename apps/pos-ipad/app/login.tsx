import { router, type Href, useFocusEffect } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Keyboard } from "react-native";

import type { RoutedScanEvent } from "@/core/peripherals/scanner";
import type { ExpoPosUpdateIdentity } from "@/core/runtime/expo-pos-runtime";
import {
  type NativeAppVersion,
  otaVersionLabel,
  UNKNOWN_NATIVE_APP_VERSION,
} from "@/core/runtime/native-app-version";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  resolveAttendanceAuditRuntimeFactory,
  type AttendanceQrDisplaySource,
} from "@/features/attendance-audit/attendance-audit-runtime";
import {
  buildCashierLoginDiagnostic,
  type CashierLoginDiagnostic,
  type CashierLoginVersionInfo,
  CashierLoginController,
  CashierLoginScreen,
  isCashierLoginRuntimeReady,
  toCashierLoginLogDraft,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { BleScannerLoginPanel } from "@/features/settings/ble-scanner-settings";
import { toggleAppLanguage } from "@/i18n";
import {
  RouteHidScannerCapture,
  useRouteBleScan,
} from "@/ui/scanner/scanner-route-bridge";
import { usePosThemeVersion } from "@/ui/theme";

export default function LoginRoute() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const versionInfo = toCashierLoginVersionInfo(
    runtime.services?.updateIdentity,
    runtime.services?.nativeAppVersion,
  );
  const { i18n } = useTranslation();
  const scanInFlight = useRef(false);
  // 可见条码框会 autoFocus，首帧先禁用隐藏输入，避免两者竞争焦点。
  const [manualInputActive, setManualInputActive] = useState(true);
  // 蓝牙扫码枪面板：原生包不带 BLE 服务（旧包）时没有入口。
  const bleScanner = runtime.services?.scanner?.ble ?? null;
  const [bleScannerOpen, setBleScannerOpen] = useState(false);
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
    async (barcode: string, trigger: "hid-capture" | "ble") => {
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
            trigger,
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
  // 键盘模式（HID）的枪往聚焦的输入框打字，不需要租约；但 BLE 数据模式的码只投递给持有场景租约的页面。
  // 可见条码框默认 autoFocus，隐藏输入（RouteHidScannerCapture）因此大多数时间是关的，
  // 租约也随之释放，BLE 的码会被路由器丢弃（"no screen is accepting scans"）。
  // 所以登录页单独、始终持有 cashier-login 租约（只有蓝牙面板打开时让出），只收 BLE 来源的码。
  useRouteBleScan({
    context: "cashier-login",
    enabled: !bleScannerOpen,
    onScan: (value) => {
      void signInFromScanner(value, "ble");
    },
    path: "/login",
  });
  // 隐藏输入收到的 BLE 码交给上面的订阅处理，避免同一个码触发两次登录。
  const handleHidScan = useCallback(
    (value: string, source?: RoutedScanEvent["source"]) => {
      if (source === "ble") return;
      return signInFromScanner(value, "hid-capture");
    },
    [signInFromScanner],
  );
  const handleOpenBleScanner = useCallback(() => {
    // 先收起软键盘，避免盖住面板；面板打开期间扫码交给面板自己的测试 / 配对流程。
    Keyboard.dismiss();
    setBleScannerOpen(true);
  }, []);
  const handleCloseBleScanner = useCallback(() => setBleScannerOpen(false), []);
  const handleSwitchLanguage = useCallback(() => {
    void toggleAppLanguage();
  }, []);

  // 员工到店时 iPad 常停在登录页：设备级考勤码不依赖收银员会话，只在设备已授权时运行。
  // 只在登录页聚焦时存在：登录进入收银页、进入网络检测页时销毁，由收银页的考勤码接力，
  // 保证同一时刻只有一个控制器在用本机签名身份。
  const attendanceFactory = runtime.services
    ? resolveAttendanceAuditRuntimeFactory(runtime.services)
    : null;
  const deviceReady = isCashierLoginRuntimeReady(runtime);
  const [attendanceQr, setAttendanceQr] =
    useState<AttendanceQrDisplaySource | null>(null);
  useFocusEffect(
    useCallback(() => {
      if (!deviceReady || !attendanceFactory?.createDeviceQrController) {
        return undefined;
      }
      let source: AttendanceQrDisplaySource;
      try {
        source = attendanceFactory.createDeviceQrController();
      } catch {
        // 创建失败：不显示考勤码，不影响收银员登录。
        return undefined;
      }
      source.start();
      setAttendanceQr(source);
      return () => {
        source.destroy();
        setAttendanceQr((current) => (current === source ? null : current));
      };
    }, [attendanceFactory, deviceReady]),
  );

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
        enabled={!manualInputActive && !bleScannerOpen}
        onScan={handleHidScan}
        path="/login"
      />
      <CashierLoginScreen
        attendanceQr={attendanceQr}
        language={i18n.resolvedLanguage ?? i18n.language}
        onLoginDiagnostic={recordLoginDiagnostic}
        onManualInputFocusChange={handleManualInputFocusChange}
        {...(bleScanner ? { onOpenBleScanner: handleOpenBleScanner } : {})}
        onOpenNetworkCheck={() => router.push("/network-check" as Href)}
        onSwitchLanguage={handleSwitchLanguage}
        onSuccess={() => router.replace("/sales" as Href)}
        // 登录屏幕只得到受限 runtime facade；不从路由注入门店、设备或授权票据。
        runtime={runtime}
        versionInfo={versionInfo}
      />
      {bleScannerOpen && bleScanner ? (
        <BleScannerLoginPanel
          locale={
            (i18n.resolvedLanguage ?? i18n.language).toLowerCase().startsWith("zh")
              ? "zh"
              : "en"
          }
          onClose={handleCloseBleScanner}
          router={runtime.services?.scanner?.router}
          service={bleScanner}
        />
      ) : null}
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
