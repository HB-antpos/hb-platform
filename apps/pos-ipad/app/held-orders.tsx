import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";

import type { ExpoPosRuntimeServices } from "@/core/runtime/expo-pos-runtime";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  HeldOrdersScreen,
  type HeldOrdersPresenter,
  type SharedHeldOrderRemoteRow,
  type SharedHeldOrderTakeViewResult,
  type SharedHeldOrdersViewPort,
} from "@/features/held-orders";
import {
  SharedHeldOrderCoordinatorError,
  type SharedHeldOrderTakeResult,
} from "@/features/shared-held-orders/shared-held-order-coordinator";
import type { SharedHeldOrderPendingListItem } from "@/features/shared-held-orders/shared-held-order-network-api";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type HeldOrdersPresenterBinding = Readonly<{
  services: ExpoPosRuntimeServices;
  cashier: object;
  presenter: HeldOrdersPresenter;
}>;

/** 组合根持有收银员身份，挂单路由只请求受限 presenter。 */
export default function HeldOrdersRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const [binding, setBinding] =
    useState<HeldOrdersPresenterBinding | null>(null);
  const [presenterCreationFailed, setPresenterCreationFailed] = useState(false);
  const presenter =
    binding?.services === runtime.services &&
    binding.cashier === activeCashier
      ? binding.presenter
      : null;

  useEffect(() => {
    if (
      gate !== "check-device-identity" ||
      !activeCashier ||
      !runtime.services
    ) {
      setBinding(null);
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: HeldOrdersPresenter | null = null;
    const services = runtime.services;
    const cashier = activeCashier;
    setBinding(null);
    setPresenterCreationFailed(false);
    try {
      createdPresenter = services.heldOrders.createPresenter();
      attachSharedHeldOrders(createdPresenter, services);
      if (cancelled) {
        createdPresenter.destroy();
        createdPresenter = null;
      } else {
        setBinding({ services, cashier, presenter: createdPresenter });
      }
    } catch {
      if (!cancelled) {
        clearActiveCashier();
        setPresenterCreationFailed(true);
      }
    }

    return () => {
      cancelled = true;
      createdPresenter?.destroy();
      createdPresenter = null;
    };
  }, [activeCashier, clearActiveCashier, gate, runtime.services]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (presenterCreationFailed) {
    return <Redirect href={"/login" as Href} />;
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <HeldOrdersScreen
      onBack={() => router.dismissTo("/sales" as Href)}
      presenter={presenter}
    />
  );
}

/**
 * 组合根已提供 sharedHeldOrders（api + createCoordinator）；presenter 只接收
 * 视图端口，绝不直接触碰数据库或 wire DTO。共享状态只读口不返回 payload；
 * 强制释放仅在组合根已经包好 History.Recall 主管授权时暴露。
 */
function attachSharedHeldOrders(
  presenter: HeldOrdersPresenter,
  services: ExpoPosRuntimeServices,
): void {
  const shared = services.sharedHeldOrders;
  if (!shared || typeof presenter.attachSharedOrders !== "function") return;
  const coordinator = shared.createCoordinator();
  const port: SharedHeldOrdersViewPort = {
    listRemotePending: () =>
      shared.api
        .listPending()
        .then((rows) => rows.map(toSharedHeldOrderRemoteRow)),
    listLocalShareState: () => shared.listLocalShareState(),
    requestShare: (holdGuid) => shared.requestShare(holdGuid),
    takeRemoteHold: (holdGuid) =>
      mapSharedHeldOrderTake(coordinator.takeRemoteHold(holdGuid)),
    recallLocalPublication: (holdGuid) =>
      mapSharedHeldOrderTake(coordinator.recallLocalPublication(holdGuid)),
    cancelOwnedHold: (holdGuid) => coordinator.cancelOwnedHold(holdGuid),
    releaseOwnedClaim: async (holdGuid) => {
      try {
        await coordinator.ownerRelease(holdGuid);
        return true;
      } catch (error: unknown) {
        if (
          error instanceof SharedHeldOrderCoordinatorError &&
          error.code === "NOT_FOUND"
        ) {
          return false;
        }
        throw error;
      }
    },
    ...(coordinator.forceRelease
      ? {
          forceRelease: ({ holdGuid, reason }) =>
            coordinator.forceRelease!(holdGuid, reason),
        }
      : {}),
  };
  presenter.attachSharedOrders(port);
}

function toSharedHeldOrderRemoteRow(
  row: SharedHeldOrderPendingListItem,
): SharedHeldOrderRemoteRow {
  return {
    holdGuid: row.holdGuid,
    deviceCode: row.deviceCode,
    cashierName: row.heldByCashierName,
    heldAtIso: row.heldAtIso,
    lineCount: row.lineCount,
    actualCents: row.actualCents,
  };
}

function mapSharedHeldOrderTake(
  result: Promise<SharedHeldOrderTakeResult>,
): Promise<SharedHeldOrderTakeViewResult> {
  return result.then((taken) => ({
    holdGuid: taken.holdGuid,
    ok: taken.outcome === "restored",
    outcome: taken.outcome,
  }));
}
