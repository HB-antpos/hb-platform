import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  DailyCloseScreen,
  DailyCloseUnavailableScreen,
  resolveDailyCloseAccess,
  resolveDailyCloseRuntimeFactory,
  type DailyClosePresenter,
} from "@/features/daily-close";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type DailyCloseBinding = Readonly<{
  cashier: object;
  presenter: DailyClosePresenter;
  services: object;
}>;

/**
 * 路由只做直链 UX 门禁与设备绑定复核；可信 cashier lease、门店时区、
 * repository 和打印 Port 全部留在零参数 runtime factory 内部。
 */
export default function DailyCloseRoute() {
  usePosThemeVersion();
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
  const access = resolveDailyCloseAccess(
    activeCashier?.permissions ?? [],
  );
  const factory = runtime.services
    ? resolveDailyCloseRuntimeFactory(runtime.services)
    : null;
  const [binding, setBinding] =
    useState<DailyCloseBinding | null>(null);
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
    let createdPresenter: DailyClosePresenter | null = null;
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
      <DailyCloseUnavailableScreen
        onBack={() => router.dismissTo("/sales" as Href)}
      />
    );
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <DailyCloseScreen
      onBack={() => router.dismissTo("/sales" as Href)}
      presenter={presenter}
    />
  );
}
