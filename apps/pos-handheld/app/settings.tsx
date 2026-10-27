import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";
import { resolveSyncHistoryAccess } from "@hb/pos-sync";

import { getApiEntryModeCoordinator } from "@/core/api/api-entry-mode-store";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  resolveSettingsAccess,
  resolveSettingsRuntimeFactory,
  SettingsScreen,
  SettingsUnavailableScreen,
  type SettingsPresenter,
} from "@/features/settings";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";

type SettingsBinding = Readonly<{
  cashier: object;
  presenter: SettingsPresenter;
  services: object;
}>;

/**
 * 直链 Settings 先执行与销售页相同的设备/收银员门禁，再调用零参数工厂。
 * 可信 lease、细分权限复核、数据库与硬件端口都留在组合根，不能由路由传入。
 */
export default function SettingsRoute() {
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore(
    (state) => state.activeCashier,
  );
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const gate = resolveProtectedSalesRouteGate(
    runtime.state,
    activeCashier,
  );
  const access = resolveSettingsAccess(
    activeCashier?.permissions ?? [],
  );
  const syncHistoryAccess = resolveSyncHistoryAccess(
    activeCashier?.permissions ?? [],
  );
  const factory = runtime.services
    ? resolveSettingsRuntimeFactory(runtime.services)
    : null;
  const [binding, setBinding] = useState<SettingsBinding | null>(null);
  const [runtimeUnavailable, setRuntimeUnavailable] = useState(false);
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
      !access.canView
    ) {
      setBinding(null);
      setRuntimeUnavailable(false);
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: SettingsPresenter | null = null;
    const cashier = activeCashier;
    const services = runtime.services;
    setBinding(null);
    setRuntimeUnavailable(false);
    void services.deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (cancelled) return;
        if (
          !identity ||
          !isActiveCashierBoundToDevice(cashier, identity)
        ) {
          clearActiveCashier();
          return;
        }
        if (!factory) {
          setRuntimeUnavailable(true);
          return;
        }
        try {
          createdPresenter = factory.createPresenter();
        } catch {
          setRuntimeUnavailable(true);
          return;
        }
        if (cancelled) {
          createdPresenter.destroy();
          createdPresenter = null;
          return;
        }
        setBinding({ cashier, presenter: createdPresenter, services });
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
    access.canView,
    activeCashier,
    clearActiveCashier,
    factory,
    gate,
    runtime.services,
  ]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (!access.canView) {
    return <Redirect href={"/sales" as Href} />;
  }
  if (runtimeUnavailable) {
    return (
      <SettingsUnavailableScreen
        onBack={() => router.dismissTo("/sales" as Href)}
      />
    );
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <SettingsScreen
      // 收银入口（线路）设置：与运行时共用同一个进程内协调器，手动覆盖才能压过后台下发值。
      apiEntry={getApiEntryModeCoordinator()}
      onBack={() => router.dismissTo("/sales" as Href)}
      {...(syncHistoryAccess.canView
        ? { onOpenSyncHistory: () => router.push("/sync-history" as Href) }
        : {})}
      presenter={presenter}
      {...(runtime.services?.scanner?.router
        ? { scanner: runtime.services.scanner.router }
        : {})}
    />
  );
}
