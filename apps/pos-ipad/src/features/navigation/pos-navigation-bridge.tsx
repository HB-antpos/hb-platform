import { type Href, usePathname, useRouter } from "expo-router";
import {
  type PropsWithChildren,
  useCallback,
  useMemo,
  useRef,
  useState,
} from "react";
import { useTranslation } from "react-i18next";

import {
  passesPosDestinationGuard,
  POS_DESTINATION_ROUTES,
  resolvePosDestinationAccess,
} from "./pos-destination-access";

import {
  formatAppVersionLabel,
  otaVersionLabel,
  UNKNOWN_NATIVE_APP_VERSION,
} from "@/core/runtime/native-app-version";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { useCashierLoginStore } from "@/features/cashier-login";
import { toggleAppLanguage } from "@/i18n";
import {
  readPinnedDestinations,
  savePinnedDestinations,
} from "@/ui/preferences/terminal-ui-preferences";
import type { PosNavEntry, PosNavModel } from "@/ui/shell/pos-app-rail";
import {
  POS_DESTINATION_IDS,
  reconcilePinnedDestinations,
  type PosDestinationId,
} from "@/ui/shell/pos-navigation";
import {
  PosNavigationContext,
  type PosNavigationContextValue,
} from "@/ui/shell/pos-navigation-context";

const SALES_PATH = "/sales";

const LABEL_KEYS: Readonly<Record<PosDestinationId, string>> = {
  "held-orders": "heldOrders",
  returns: "returns",
  installments: "installments",
  "payment-recovery": "paymentRecovery",
  "local-history": "localHistory",
  "remote-history": "remoteHistory",
  "sync-history": "syncHistory",
  "daily-close": "dailyClose",
  "attendance-audit": "attendanceAudit",
  "catalog-maintenance": "catalogMaintenance",
  settings: "settings",
};

/**
 * 给所有页面提供左侧栏的导航模型：按当前收银员权限决定显示哪些入口，
 * 并统一处理跳转——从收银页进入是压栈，从其他页面互相跳转是替换当前页，
 * 这样无论怎么跳，「返回收银」都只需要一步。
 */
export function PosNavigationBridge({ children }: PropsWithChildren) {
  const router = useRouter();
  const pathname = usePathname();
  const pathnameRef = useRef(pathname);
  pathnameRef.current = pathname;
  const runtime = usePosRuntime();
  const { i18n, t } = useTranslation();
  const language = i18n.resolvedLanguage ?? i18n.language;
  const permissions = useCashierLoginStore(
    (state) => state.activeCashier?.permissions ?? null,
  );
  const [pinned, setPinned] = useState<PosDestinationId[]>(() =>
    reconcilePinnedDestinations(readPinnedDestinations()),
  );

  const openSales = useCallback(() => {
    if (pathnameRef.current !== SALES_PATH) {
      router.dismissTo(SALES_PATH as Href);
    }
  }, [router]);

  const openDestination = useCallback(
    (destinationId: PosDestinationId) => {
      const onSales = pathnameRef.current === SALES_PATH;
      if (!passesPosDestinationGuard(destinationId)) {
        // 检查没通过时回到收银页，让收银员先处理那里弹出的待办。
        if (!onSales) router.dismissTo(SALES_PATH as Href);
        return;
      }
      const href = POS_DESTINATION_ROUTES[destinationId] as Href;
      if (onSales) router.push(href);
      else router.replace(href);
    },
    [router],
  );

  const handlePinnedChange = useCallback((next: PosDestinationId[]) => {
    const reconciled = reconcilePinnedDestinations(next);
    setPinned(reconciled);
    void savePinnedDestinations(reconciled).catch(() => undefined);
  }, []);

  const switchLanguage = useCallback(() => {
    void toggleAppLanguage();
  }, []);

  const model = useMemo<PosNavModel>(() => {
    const access = resolvePosDestinationAccess(permissions);
    const entries: PosNavEntry[] = POS_DESTINATION_IDS.filter((id) =>
      access.has(id),
    ).map((id) => ({
      description: t(`nav.desc.${LABEL_KEYS[id]}`),
      id,
      label: t(`nav.${LABEL_KEYS[id]}`),
      onPress: () => openDestination(id),
    }));
    return {
      entries,
      onOpenSales: openSales,
      onPinnedChange: handlePinnedChange,
      onSwitchLanguage: switchLanguage,
      pinned,
    };
    // language 变化时 t 的引用不一定变，显式依赖它以刷新文案。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    handlePinnedChange,
    language,
    openDestination,
    openSales,
    permissions,
    pinned,
    switchLanguage,
    t,
  ]);

  const services = runtime.services;
  const appVersionLabel = services
    ? formatAppVersionLabel(
        services.nativeAppVersion ?? UNKNOWN_NATIVE_APP_VERSION,
        otaVersionLabel(services.updateIdentity),
      )
    : null;

  const value = useMemo<PosNavigationContextValue>(
    () => ({ appVersionLabel, language, model, openSales, switchLanguage }),
    [appVersionLabel, language, model, openSales, switchLanguage],
  );

  return (
    <PosNavigationContext.Provider value={value}>
      {children}
    </PosNavigationContext.Provider>
  );
}
