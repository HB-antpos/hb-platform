import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";

import type { ExpoPosRuntimeServices } from "@/core/runtime/expo-pos-runtime";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  type ActiveCashierSummary,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  canDownloadCatalog,
  CatalogMaintenancePresenter,
  CatalogMaintenanceScreen,
} from "@/features/catalog/maintenance";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type CatalogMaintenanceBinding = Readonly<{
  services: ExpoPosRuntimeServices;
  cashier: ActiveCashierSummary;
  presenter: CatalogMaintenancePresenter;
}>;

/** 目录更新只能使用当前已认证设备的门店，界面不能覆盖 storeCode。 */
export default function CatalogMaintenanceRoute() {
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
    canDownloadCatalog(activeCashier.permissions);
  const [binding, setBinding] =
    useState<CatalogMaintenanceBinding | null>(null);
  const presenter =
    binding?.services === runtime.services &&
    binding.cashier === activeCashier
      ? binding.presenter
      : null;

  useEffect(() => {
    if (
      gate !== "check-device-identity" ||
      !activeCashier ||
      !runtime.services ||
      !authorized
    ) {
      setBinding(null);
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: CatalogMaintenancePresenter | null = null;
    const services = runtime.services;
    const cashier = activeCashier;
    setBinding(null);
    void services.deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (cancelled) return;
        if (!identity || !isActiveCashierBoundToDevice(cashier, identity)) {
          clearActiveCashier();
          return;
        }
        const presenter = new CatalogMaintenancePresenter({
          authenticatedStoreCode: identity.storeCode,
          coordinator: services.catalogRefresh,
          port: services.catalog,
        });
        createdPresenter = presenter;
        if (cancelled) {
          createdPresenter.destroy();
          createdPresenter = null;
          return;
        }
        setBinding({ services, cashier, presenter });
        // 中文注释：本地目录摘要异步读取，不能阻塞已授权收银员进入维护界面。
        void presenter.initialize();
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
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <CatalogMaintenanceScreen
      onBack={() => router.dismissTo("/sales" as Href)}
      presenter={presenter}
    />
  );
}
