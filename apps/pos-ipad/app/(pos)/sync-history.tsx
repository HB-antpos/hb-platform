import { File, Paths } from "expo-file-system";
import { Redirect, type Href, useRouter } from "expo-router";
import * as Sharing from "expo-sharing";
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
  SyncHistoryScreen,
  type SyncHistoryPresenter,
} from "@/features/sync-history";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

type SyncHistoryPresenterBinding = Readonly<{
  services: ExpoPosRuntimeServices;
  cashier: ActiveCashierSummary;
  presenter: SyncHistoryPresenter;
}>;

const SUPPORT_EXPORT_FILE_NAME = "hb-pos-sync-support.json";

async function shareSupportExport(serializedJson: string): Promise<void> {
  const file = new File(Paths.cache, SUPPORT_EXPORT_FILE_NAME);
  try {
    file.create({ intermediates: true, overwrite: true });
    file.write(serializedJson);
    if (!(await Sharing.isAvailableAsync())) {
      throw new Error("support-export-sharing-unavailable");
    }
    await Sharing.shareAsync(file.uri, {
      UTI: "public.json",
      dialogTitle: "HB POS support export",
      mimeType: "application/json",
    });
  } finally {
    // 中文注释：支持包只在缓存中短暂存在；分享成功、取消或失败都立即清理。
    if (file.exists) file.delete();
  }
}

/** 本地历史同样复核当前设备绑定，避免锁机或切换设备后继续读取收银账本。 */
export default function SyncHistoryRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const [binding, setBinding] =
    useState<SyncHistoryPresenterBinding | null>(null);
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
    let createdPresenter: SyncHistoryPresenter | null = null;
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
        createdPresenter = services.syncHistory.createPresenter(
          cashier.permissions,
        );
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
  }, [activeCashier, clearActiveCashier, gate, runtime.services]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <SyncHistoryScreen
      onBack={() => router.dismissTo("/sales" as Href)}
      onExport={shareSupportExport}
      presenter={presenter}
    />
  );
}
