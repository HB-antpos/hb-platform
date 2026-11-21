import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  type ActiveCashierSummary,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  hasLocalHistoryViewPermission,
  LocalHistoryScreen,
  LocalHistoryUnavailableScreen,
  resolveLocalHistoryPresenterFactory,
  type LocalHistoryPresenter,
} from "@/features/local-history";
import { useRouteBleScan } from "@/ui/scanner/scanner-route-bridge";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type LocalHistoryBinding = Readonly<{
  services: object;
  cashier: ActiveCashierSummary;
  presenter: LocalHistoryPresenter;
}>;

/**
 * 本机历史只在设备身份再次通过后读取 SQLCipher；门店、终端和权限都由
 * 生产组合根绑定，离线状态不会降级为未受保护的数据访问。
 */
export default function LocalHistoryRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const authorized =
    activeCashier !== null &&
    hasLocalHistoryViewPermission(activeCashier.permissions);
  const [binding, setBinding] = useState<LocalHistoryBinding | null>(null);
  const [integrationUnavailable, setIntegrationUnavailable] = useState(false);
  const presenter =
    binding?.services === runtime.services &&
    binding.cashier === activeCashier
      ? binding.presenter
      : null;
  // BLE 扫码枪：扫小票短码 / 订单号条码，直接填入关键字并筛选；键盘模式扫码枪仍直接打进输入框。
  const [scannedKeyword, setScannedKeyword] = useState<Readonly<{
    id: number;
    value: string;
  }> | null>(null);
  useRouteBleScan({
    path: "/local-history",
    enabled: presenter !== null,
    onScan: (value) =>
      setScannedKeyword((previous) => ({ id: (previous?.id ?? 0) + 1, value })),
  });

  useEffect(() => {
    if (
      gate !== "check-device-identity" ||
      !activeCashier ||
      !runtime.services ||
      !authorized
    ) {
      setBinding(null);
      setIntegrationUnavailable(false);
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: LocalHistoryPresenter | null = null;
    const services = runtime.services;
    const cashier = activeCashier;
    setBinding(null);
    setIntegrationUnavailable(false);
    void services.deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (cancelled) return;
        if (!identity || !isActiveCashierBoundToDevice(cashier, identity)) {
          clearActiveCashier();
          return;
        }
        const factory = resolveLocalHistoryPresenterFactory(services);
        if (!factory) {
          setIntegrationUnavailable(true);
          return;
        }
        try {
          createdPresenter = factory.createPresenter();
        } catch {
          setIntegrationUnavailable(true);
          return;
        }
        if (cancelled) {
          createdPresenter.destroy();
          createdPresenter = null;
          return;
        }
        setBinding({ services, cashier, presenter: createdPresenter });
      })
      .catch(() => {
        if (!cancelled) clearActiveCashier();
      });

    return () => {
      cancelled = true;
      createdPresenter?.destroy();
      createdPresenter = null;
    };
  }, [
    activeCashier,
    authorized,
    clearActiveCashier,
    gate,
    runtime.services,
  ]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (!authorized) {
    return <Redirect href={"/sales" as Href} />;
  }
  if (integrationUnavailable) {
    return (
      <LocalHistoryUnavailableScreen
        onBack={() => router.dismissTo("/sales" as Href)}
      />
    );
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <LocalHistoryScreen
      scannedKeyword={scannedKeyword}
      onBack={() => router.dismissTo("/sales" as Href)}
      presenter={presenter}
    />
  );
}
