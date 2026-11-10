import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useRef, useState } from "react";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  resolveSpecialProductsAccess,
  resolveSpecialProductsRuntimeFactory,
  SpecialProductsScreen,
  SpecialProductsUnavailableScreen,
  type SpecialProductsPresenter,
} from "@/features/special-products";
import { usePosSound } from "@/ui/feedback/pos-sound-context";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type SpecialProductsBinding = Readonly<{
  cashier: object;
  presenter: SpecialProductsPresenter;
  services: object;
}>;

/**
 * 直链与销售入口执行同一设备/收银员门禁；factory 仅为 Wave4 组合根未接线期间
 * 的窄桥接点，真实权限、门店、cart lease 必须继续由 factory 内部复核。
 */
export default function SpecialProductsRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const access = resolveSpecialProductsAccess(
    activeCashier?.permissions ?? [],
  );
  const factory = runtime.services
    ? resolveSpecialProductsRuntimeFactory(runtime.services)
    : null;
  const [binding, setBinding] = useState<SpecialProductsBinding | null>(null);
  const [runtimeUnavailable, setRuntimeUnavailable] = useState(false);
  const [presenterCreationFailed, setPresenterCreationFailed] =
    useState(false);
  // 网络信号来源：connectivity 由 NetworkStatusBridge 后端探测驱动，
  // 后端恢复后会自动翻转为 online（30s 周期 / App 前台恢复）。
  // runtime.state.backend 在启动后不会更新，无法驱动离线→在线恢复。
  const connectivity = usePosShellStore(
    (state) => state.connectivity,
  );
  const prevOnline = useRef<boolean | null>(null);
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
      setPresenterCreationFailed(false);
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: SpecialProductsPresenter | null = null;
    const services = runtime.services;
    const cashier = activeCashier;
    setBinding(null);
    setRuntimeUnavailable(false);
    setPresenterCreationFailed(false);
    void services.deviceSession
      .getDeviceIdentity()
      .then((identity) => {
        if (cancelled) return;
        if (!identity || !isActiveCashierBoundToDevice(cashier, identity)) {
          clearActiveCashier();
          return;
        }
        if (!factory) {
          setRuntimeUnavailable(true);
          return;
        }
        try {
          createdPresenter = factory.createPresenter();
          if (cancelled) {
            createdPresenter.destroy();
            createdPresenter = null;
            return;
          }
          setBinding({ cashier, presenter: createdPresenter, services });
        } catch {
          if (!cancelled) {
            clearActiveCashier();
            setPresenterCreationFailed(true);
          }
        }
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

  // 网络从离线恢复为在线时，自动刷新特殊商品数据并恢复下载可用，无需用户手动操作。
  useEffect(() => {
    if (!presenter) return;
    // checking 视为在线（未知时乐观），仅明确 offline 才锁定管理操作。
    const nextOnline =
      connectivity === "online" || connectivity === "checking";
    const wasOffline = prevOnline.current === false;
    presenter.setOnline(nextOnline);
    if (wasOffline && nextOnline) {
      // 离线期间列表可能已陈旧，恢复后重新加载本地列表与在线能力。
      void presenter.load().catch(() => undefined);
    }
    prevOnline.current = nextOnline;
  }, [connectivity, presenter]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login" || presenterCreationFailed) {
    return <Redirect href={"/login" as Href} />;
  }
  if (!access.canView) {
    return <Redirect href={"/sales" as Href} />;
  }
  if (runtimeUnavailable) {
    return (
      <SpecialProductsUnavailableScreen
        onBack={() => router.dismissTo("/sales" as Href)}
      />
    );
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <>
      <SpecialProductsSoundBridge
        presenter={presenter}
        router={router}
      />
      <SpecialProductsScreen
        onBack={() => router.dismissTo("/sales" as Href)}
        presenter={presenter}
      />
    </>
  );
}

function SpecialProductsSoundBridge({
  presenter,
  router,
}: Readonly<{
  presenter: SpecialProductsPresenter;
  router: { dismissTo(href: Href): void };
}>) {
  const { play } = usePosSound();
  // 一次性守卫：同一会话内连点加购（added→incremented）只跳转一次，
  // 避免退场动画期间的重复 dismissTo
  const returnedRef = useRef(false);
  useEffect(
    () =>
      presenter.subscribeFeedback((event) => {
        switch (event.kind) {
          case "query-found":
          case "query-empty":
          case "query-error":
            play(event.kind);
            return;
          case "added":
            play("cart-added");
            if (!returnedRef.current) {
              returnedRef.current = true;
              // 加购成功自动返回收银页（与 WPF 行为一致）
              router.dismissTo("/sales" as Href);
            }
            return;
          case "incremented":
            play("cart-incremented");
            if (!returnedRef.current) {
              returnedRef.current = true;
              // 数量累加同样视为加购成功，返回收银页
              router.dismissTo("/sales" as Href);
            }
            return;
          case "failed-blocked":
            play("cart-failed-blocked");
        }
      }),
    [play, presenter, router],
  );
  return null;
}
