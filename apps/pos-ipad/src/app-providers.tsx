import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { PropsWithChildren } from "react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { StatusBar } from "react-native";
import { PaperProvider } from "react-native-paper";
import { SafeAreaProvider } from "react-native-safe-area-context";

import "@/i18n";
import {
  ApplicationLogErrorBoundary,
  installApplicationErrorObserver,
} from "@/core/logging/application-error-observer";
import {
  PosRuntimeProvider,
  usePosRuntime,
} from "@/core/runtime/pos-runtime-context";
import { AppUpdateGateBridge } from "@/features/app-updates";
import { CashierSessionInvalidationBridge } from "@/features/cashier-login";
import { OperationAuthorizationModal } from "@/features/operation-authorization/operation-authorization-modal";
import { PosSoundProvider } from "@/ui/feedback/pos-sound-provider";
import {
  readStoredTheme,
  saveStoredTheme,
} from "@/ui/preferences/terminal-ui-preferences";
import { NetworkStatusBridge } from "@/ui/shell/network-status-bridge";
import { PeripheralStatusBridge } from "@/ui/shell/peripheral-status-bridge";
import { RuntimeStatusBridge } from "@/ui/shell/runtime-status-bridge";
import { RuntimeWorkBridge } from "@/ui/shell/runtime-work-bridge";
import { applyPosTheme, buildPosPaperTheme, type PosThemeId, usePosTheme, usePosThemeVersion } from "@/ui/theme";
import {
  PosThemeControllerContext,
  type PosThemeController,
} from "@/ui/theme-controller";

// 在任何页面渲染之前同步恢复上次选的主题，避免启动时先闪一下默认配色。
const storedThemeId = readStoredTheme();
if (storedThemeId) applyPosTheme(storedThemeId);

const persistentThemeController: PosThemeController = {
  selectTheme(themeId: PosThemeId) {
    applyPosTheme(themeId);
    void saveStoredTheme(themeId).catch(() => undefined);
  },
};

export function AppProviders({ children }: PropsWithChildren) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            retry: 1,
            staleTime: 30_000,
          },
          mutations: {
            retry: false,
          },
        },
      }),
  );

  const activeTheme = usePosTheme();
  const paperTheme = useMemo(
    () => buildPosPaperTheme(activeTheme.id),
    [activeTheme.id],
  );

  return (
    <SafeAreaProvider>
      {/* 顶栏在所有主题下都是深色，系统状态栏文字固定用浅色。 */}
      <StatusBar barStyle="light-content" />
      <QueryClientProvider client={queryClient}>
        <PaperProvider theme={paperTheme}>
          <PosThemeControllerContext.Provider value={persistentThemeController}>
          <PosSoundProvider>
            <PosRuntimeProvider>
              <ApplicationErrorObservationBridge>
                <CashierSessionInvalidationBridge />
                <NetworkStatusBridge />
                <PeripheralStatusBridge />
                <RuntimeStatusBridge />
                <RuntimeWorkBridge />
                <OperationAuthorizationModalBridge />
                {children}
                <AppUpdateGateBridge />
              </ApplicationErrorObservationBridge>
            </PosRuntimeProvider>
          </PosSoundProvider>
          </PosThemeControllerContext.Provider>
        </PaperProvider>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}

function ApplicationErrorObservationBridge({ children }: PropsWithChildren) {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const applicationLogRef = useRef(runtime.services?.applicationLog ?? null);
  applicationLogRef.current = runtime.services?.applicationLog ?? null;

  useEffect(
    () => installApplicationErrorObserver(() => applicationLogRef.current),
    [],
  );

  return (
    <ApplicationLogErrorBoundary applicationLog={applicationLogRef.current}>
      {children}
    </ApplicationLogErrorBoundary>
  );
}

function OperationAuthorizationModalBridge() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const authorization = runtime.services?.operationAuthorization;
  if (!authorization || authorization.status !== "available") return null;

  return (
    <OperationAuthorizationModal
      locale={i18n.resolvedLanguage ?? i18n.language}
      service={authorization}
    />
  );
}
