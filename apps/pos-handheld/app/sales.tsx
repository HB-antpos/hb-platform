import * as Crypto from "expo-crypto";
import { Redirect, type Href, useFocusEffect, useRouter } from "expo-router";
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import { StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import type { NewTransactionGate } from "@/core/contracts/app-updates";
import { businessStartupClock } from "@/core/performance/business-startup-clock";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import type { PosAuthorizedFulfilmentActionResult } from "@/core/runtime/production-pos-service-composition";
import {
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { canDownloadCatalog } from "@/features/catalog/maintenance";
import { DAILY_CLOSE_VIEW_PERMISSION } from "@/features/daily-close";
import {
  INSTALLMENTS_VIEW_PERMISSION,
  resolveInstallmentsRuntimeFactory,
} from "@/features/installments";
import { LOCAL_HISTORY_VIEW_PERMISSION } from "@/features/local-history/local-history-presenter";
import { REMOTE_HISTORY_VIEW_PERMISSION } from "@/features/remote-history";
import { scanTiming } from "@/features/sales/runtime/scan-timing";
import { resolveTrustedProductImageUri } from "@hb/pos-domain/features/sales/runtime/trusted-product-image-uri";
import {
  resolveSalesLocale,
  type SalesPresenter,
  SalesScreen,
  type SalesToolbarActionId,
  type SalesUtilityActionResult,
} from "@/features/sales/ui";
import { reconcileSalesToolbarOrder } from "@/features/sales/ui/sales-toolbar-order";
import { CameraScannerModal } from "@/features/scanner-camera";
import { SETTINGS_VIEW_PERMISSION } from "@/features/settings";
import { SPECIAL_PRODUCTS_VIEW_PERMISSION } from "@/features/special-products";
import { toggleAppLanguage } from "@/i18n";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { usePosSound } from "@/ui/feedback/pos-sound-context";
import {
  readSalesToolbarOrder,
  saveSalesToolbarOrder,
} from "@/ui/preferences/terminal-ui-preferences";
import { RouteHidScannerCapture } from "@/ui/scanner/scanner-route-bridge";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type SalesPresenterBinding = Readonly<{
  services: object;
  cashier: object;
  presenter: SalesPresenter;
}>;

type SalesBootstrapFailureStage =
  "payment-recovery" | "installment-recovery" | "presenter";

const UNCHECKED_UPDATE_GATE: NewTransactionGate = Object.freeze({
  state: "unchecked",
  canStartNewTransaction: true,
  canContinueRecovery: true,
});

/** 组合根确认当前收银员后，路由只创建其受限销售 presenter。 */
export default function SalesRoute() {
  const { push, replace } = useRouter();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const subscribeUpdateGate = useCallback(
    (listener: () => void) =>
      runtime.services?.appUpdates?.subscribe(listener) ?? (() => undefined),
    [runtime.services],
  );
  const getUpdateGate = useCallback(
    () => runtime.services?.appUpdates?.getGate() ?? UNCHECKED_UPDATE_GATE,
    [runtime.services],
  );
  const updateGate = useSyncExternalStore(
    subscribeUpdateGate,
    getUpdateGate,
    getUpdateGate,
  );
  const [binding, setBinding] = useState<SalesPresenterBinding | null>(null);
  const [manualInputActive, setManualInputActive] = useState(false);
  const [cameraScannerVisible, setCameraScannerVisible] = useState(false);
  const [toolbarOrder, setToolbarOrder] = useState<
    readonly SalesToolbarActionId[]
  >(() => reconcileSalesToolbarOrder(readSalesToolbarOrder()));
  const hidRestoreTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [bootstrapAttempt, setBootstrapAttempt] = useState(0);
  const [bootstrapFailure, setBootstrapFailure] =
    useState<SalesBootstrapFailureStage | null>(null);
  const clearHidRestoreTimer = useCallback(() => {
    if (hidRestoreTimer.current !== null) {
      clearTimeout(hidRestoreTimer.current);
      hidRestoreTimer.current = null;
    }
  }, []);
  const handleManualInputFocusChange = useCallback(
    (focused: boolean) => {
      clearHidRestoreTimer();
      if (focused) {
        setManualInputActive(true);
        return;
      }
      // 输入框交接会先 blur 再 focus；下一轮事件循环才恢复 HID，避免隐藏输入抢焦点。
      hidRestoreTimer.current = setTimeout(() => {
        hidRestoreTimer.current = null;
        setManualInputActive(false);
      }, 0);
    },
    [clearHidRestoreTimer],
  );
  const presenter =
    binding?.services === runtime.services && binding.cashier === activeCashier
      ? binding.presenter
      : null;
  useFocusEffect(
    useCallback(() => {
      // 支付页位于同一导航栈时销售路由不会卸载；回焦必须释放结账冻结态。
      presenter?.releasePreparedCheckout();
    }, [presenter]),
  );
  const addScannedProduct = useCallback(
    (barcode: string, source: "hid" | "camera" = "hid") => {
      if (!presenter || presenter.getState().phase !== "selling") return;
      // 路由扫码不触碰触屏草稿；每次 HID/相机输入独立启动 lookup。
      void presenter.addScannedLookupCode(barcode, source);
    },
    [presenter],
  );
  const noteHidTextChange = useCallback(() => {
    scanTiming.noteHidCharacter();
  }, []);
  const handleToolbarOrderChange = useCallback(
    (nextOrder: readonly SalesToolbarActionId[]) => {
      const reconciledOrder = reconcileSalesToolbarOrder(nextOrder);
      setToolbarOrder(reconciledOrder);
      void saveSalesToolbarOrder(reconciledOrder).catch(() => undefined);
    },
    [],
  );
  const handleSwitchLanguage = useCallback(() => {
    void toggleAppLanguage();
  }, []);
  const resolveCartProductImage = useCallback(
    async (input: Readonly<{ productCode: string; lookupCode: string }>) => {
      const services = runtime.services;
      if (!services) return null;
      const findExactCandidates = services.catalog.findExactCandidates;
      return resolveTrustedCartProductImage({
        ...input,
        apiBaseUrl: services.apiBaseUrl,
        findExact: services.catalog.findExact,
        ...(findExactCandidates ? { findExactCandidates } : {}),
      });
    },
    [runtime.services],
  );

  useEffect(() => clearHidRestoreTimer, [clearHidRestoreTimer]);

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
    let createdPresenter: SalesPresenter | null = null;
    const services = runtime.services;
    const cashier = activeCashier;
    setBinding(null);
    setBootstrapFailure(null);
    const failBootstrap = (
      stage: SalesBootstrapFailureStage,
      error: unknown,
    ): void => {
      if (cancelled) return;
      businessStartupClock.fail();
      // 初始化故障不等同于 401/403；会话失效只能由全局 invalidation bridge 处理。
      (
        services as typeof services & {
          applicationLog?: {
            record(entry: {
              category: string;
              error: unknown;
              level: "Error";
              message: string;
              properties: { stage: SalesBootstrapFailureStage };
            }): void;
          };
        }
      ).applicationLog?.record({
        level: "Error",
        message: "Sales bootstrap failed.",
        category: "sales.bootstrap",
        error,
        properties: { stage },
      });
      setBootstrapFailure(stage);
    };
    const createSalesPresenter = () => {
      try {
        createdPresenter = services.sales.createPresenter();
        if (cancelled) {
          createdPresenter.destroy();
          createdPresenter = null;
        } else {
          setBinding({ services, cashier, presenter: createdPresenter });
        }
      } catch (error: unknown) {
        failBootstrap("presenter", error);
      }
    };

    // 两套支付账本都可能持有活动购物车或外部授权。普通支付先检查并拥有
    // 恢复优先权；只有确认其稳定后才读取分期账本，期间绝不短暂开放销售编辑。
    void (async () => {
      if (services.payments.status === "available") {
        try {
          if (await services.payments.hasRecoveryRequired()) {
            if (!cancelled) replace("/payment" as Href);
            return;
          }
        } catch (error: unknown) {
          failBootstrap("payment-recovery", error);
          return;
        }
      }
      try {
        const installmentFactory = resolveInstallmentsRuntimeFactory(services);
        if (
          installmentFactory &&
          (await installmentFactory.hasRecoveryRequired())
        ) {
          if (!cancelled) replace("/payment" as Href);
          return;
        }
      } catch (error: unknown) {
        failBootstrap("installment-recovery", error);
        return;
      }
      if (!cancelled) createSalesPresenter();
    })();
    return () => {
      cancelled = true;
      createdPresenter?.destroy();
      createdPresenter = null;
    };
  }, [activeCashier, bootstrapAttempt, gate, replace, runtime.services]);

  const retryBootstrap = useCallback(() => {
    setBootstrapFailure(null);
    setBootstrapAttempt((attempt) => attempt + 1);
  }, []);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (bootstrapFailure) {
    return (
      <SalesBootstrapFailureScreen
        language={i18n.resolvedLanguage ?? i18n.language}
        onRetry={retryBootstrap}
        stage={bootstrapFailure}
      />
    );
  }
  if (!presenter || !runtime.services) {
    return <BootstrapScreen />;
  }
  const services = runtime.services;
  const updatePolicy = services.appUpdates?.getPolicy() ?? null;
  const iosUpdateDownloadUrl =
    updatePolicy?.platform === "iOS" &&
    (updatePolicy.distribution === "app-store" ||
      updatePolicy.distribution === "testflight")
      ? updatePolicy.downloadUrl
      : null;

  return (
    <>
      <RouteHidScannerCapture
        context="product"
        enabled={!manualInputActive && !cameraScannerVisible}
        onHidTextChange={noteHidTextChange}
        onScan={addScannedProduct}
        path="/sales"
      />
      <SalesStartupMilestones
        newTransactionGate={updateGate}
        presenter={presenter}
      />
      <SalesSoundBridge presenter={presenter} />
      <SalesScreen
        locale={resolveSalesLocale(i18n.resolvedLanguage ?? i18n.language)}
        newTransactionGate={updateGate}
        onManualInputFocusChange={handleManualInputFocusChange}
        onOpenCameraScanner={() => setCameraScannerVisible(true)}
        resolveCartProductImage={resolveCartProductImage}
        onSwitchLanguage={handleSwitchLanguage}
        onToolbarOrderChange={handleToolbarOrderChange}
        {...(iosUpdateDownloadUrl
          ? {
              onOpenRequiredUpdate: () => {
                void services.appUpdates
                  .performSelectedUpdate()
                  .catch(() => undefined);
              },
            }
          : {})}
        {...(activeCashier?.permissions.includes(DAILY_CLOSE_VIEW_PERMISSION)
          ? {
              onOpenDailyClose: () => push("/daily-close" as Href),
            }
          : {})}
        onOpenHeldOrders={() => push("/held-orders" as Href)}
        onOpenReturns={() => push("/returns" as Href)}
        {...(activeCashier?.permissions.includes(REMOTE_HISTORY_VIEW_PERMISSION)
          ? {
              onOpenRemoteHistory: () => push("/remote-history" as Href),
            }
          : {})}
        {...(activeCashier?.permissions.includes(LOCAL_HISTORY_VIEW_PERMISSION)
          ? {
              onOpenLocalHistory: () => push("/local-history" as Href),
            }
          : {})}
        {...(activeCashier?.permissions.includes(
          SPECIAL_PRODUCTS_VIEW_PERMISSION,
        )
          ? {
              onOpenSpecialProducts: () => push("/special-products" as Href),
            }
          : {})}
        {...(activeCashier?.permissions.includes(INSTALLMENTS_VIEW_PERMISSION)
          ? {
              onOpenInstallments: () => push("/installments" as Href),
            }
          : {})}
        {...(activeCashier?.permissions.includes(SETTINGS_VIEW_PERMISSION)
          ? {
              onOpenSettings: () => push("/settings" as Href),
            }
          : {})}
        onOpenAttendanceAudit={() => push("/attendance-audit" as Href)}
        onOpenCashDrawer={async () =>
          mapSalesUtilityResult(
            await services.fulfilment.openCashDrawer.execute(),
          )
        }
        onOpenPayment={(cart) => {
          if (
            cart.lines.length === 0 ||
            !Number.isSafeInteger(cart.revision) ||
            cart.revision < 0 ||
            !Number.isSafeInteger(cart.actualAmount.cents) ||
            cart.actualAmount.cents <= 0
          ) {
            presenter.releasePreparedCheckout();
            return;
          }
          try {
            push({
              pathname: "/payment",
              params: {
                checkoutIntentId: Crypto.randomUUID(),
                revision: String(cart.revision),
                totalCents: String(cart.actualAmount.cents),
              },
            } as unknown as Href);
          } catch {
            presenter.releasePreparedCheckout();
          }
        }}
        onReprintReceipt={async () =>
          mapSalesUtilityResult(await services.fulfilment.reprint.execute())
        }
        onOpenSyncHistory={() => push("/sync-history" as Href)}
        presenter={presenter}
        toolbarOrder={toolbarOrder}
        {...(activeCashier && canDownloadCatalog(activeCashier.permissions)
          ? {
              onOpenCatalogMaintenance: () =>
                push("/catalog-maintenance" as Href),
            }
          : {})}
      />
      <CameraScannerModal
        context="product"
        onClose={() => setCameraScannerVisible(false)}
        onScan={(barcode) => addScannedProduct(barcode, "camera")}
        scanner={services.scanner.router}
        visible={cameraScannerVisible}
      />
    </>
  );
}

function SalesBootstrapFailureScreen({
  language,
  onRetry,
  stage,
}: Readonly<{
  language: string;
  onRetry(): void;
  stage: SalesBootstrapFailureStage;
}>) {
  const zh = language.toLowerCase().startsWith("zh");
  const code = bootstrapFailureCode(stage);
  return (
    <SafeAreaView
      accessibilityRole="alert"
      style={bootstrapFailureStyles.page}
      testID={`sales-bootstrap-failed-${stage}`}
    >
      <PosStatusStrip />
      <View style={bootstrapFailureStyles.body}>
        <View style={bootstrapFailureStyles.panel}>
          <Text style={bootstrapFailureStyles.title}>
            {zh ? "收银初始化未完成" : "Sales is not ready"}
          </Text>
          <Text style={bootstrapFailureStyles.message}>
            {zh
              ? "收银员会话仍有效。请重试；若问题持续，请联系支持人员并提供以下代码。"
              : "Your cashier session is still active. Retry, or contact support with the code below if the problem continues."}
          </Text>
          <Text style={bootstrapFailureStyles.code}>{code}</Text>
          <PosPressable
            accessibilityLabel={zh ? "重试进入收银" : "Retry sales startup"}
            accessibilityRole="button"
            onPress={onRetry}
            style={bootstrapFailureStyles.retry}
            testID="sales-bootstrap-retry"
          >
            <Text style={bootstrapFailureStyles.retryLabel}>
              {zh ? "重试" : "Retry"}
            </Text>
          </PosPressable>
        </View>
      </View>
    </SafeAreaView>
  );
}

function bootstrapFailureCode(stage: SalesBootstrapFailureStage): string {
  switch (stage) {
    case "payment-recovery":
      return "PAYMENT_RECOVERY_CHECK_FAILED";
    case "installment-recovery":
      return "INSTALLMENT_RECOVERY_CHECK_FAILED";
    case "presenter":
      return "SALES_PRESENTER_INITIALIZATION_FAILED";
  }
}

function SalesStartupMilestones({
  newTransactionGate,
  presenter,
}: Readonly<{
  newTransactionGate: NewTransactionGate;
  presenter: SalesPresenter;
}>) {
  useEffect(() => {
    // effect 仅在完整 SalesScreen 树提交后运行，代表销售页首帧已提交。
    businessStartupClock.markSalesFirstFrameCommitted();
  }, []);

  useEffect(() => {
    const markWhenInteractive = (): void => {
      const state = presenter.getState();
      const canEditCurrentTransaction =
        state.cart.lines.length > 0 ||
        newTransactionGate.canStartNewTransaction;
      if (
        state.phase === "selling" &&
        state.capabilities.catalog &&
        state.capabilities.cartEditing &&
        canEditCurrentTransaction
      ) {
        businessStartupClock.markSalesInteractive();
      }
    };
    markWhenInteractive();
    return presenter.subscribe(markWhenInteractive);
  }, [newTransactionGate.canStartNewTransaction, presenter]);

  return null;
}

function SalesSoundBridge({
  presenter,
}: Readonly<{ presenter: SalesPresenter }>) {
  const { play } = usePosSound();
  useEffect(
    () =>
      presenter.subscribeFeedback((event) => {
        const playMeasured = (cue: Parameters<typeof play>[0]): void => {
          scanTiming.expectSound(event.timingId, cue);
          play(cue);
        };
        switch (event.kind) {
          case "query-found":
          case "query-empty":
          case "query-error":
            playMeasured(event.kind);
            return;
          case "added":
            playMeasured("cart-added");
            return;
          case "incremented":
            playMeasured("cart-incremented");
            return;
          case "not-found":
            playMeasured("cart-not-found");
            return;
          case "failed-blocked":
            playMeasured("cart-failed-blocked");
        }
      }),
    [play, presenter],
  );
  return null;
}

const bootstrapFailureStyles = StyleSheet.create({
  body: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 32,
    width: "100%",
  },
  code: {
    color: posColors.red,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 0.4,
    marginBottom: 24,
  },
  message: {
    color: posColors.mutedInk,
    fontSize: 16,
    lineHeight: 24,
    marginBottom: 16,
  },
  page: {
    backgroundColor: posColors.canvas,
    flex: 1,
  },
  panel: {
    backgroundColor: "#FFFFFF",
    maxWidth: 520,
    padding: 32,
    width: "100%",
  },
  retry: {
    alignItems: "center",
    backgroundColor: posColors.blue,
    justifyContent: "center",
    minHeight: 48,
    paddingHorizontal: 20,
  },
  retryLabel: {
    color: "#FFFFFF",
    fontSize: 16,
    fontWeight: "800",
  },
  title: {
    color: posColors.ink,
    fontSize: 24,
    fontWeight: "900",
    marginBottom: 12,
  },
});

function mapSalesUtilityResult(
  result: PosAuthorizedFulfilmentActionResult,
): SalesUtilityActionResult {
  switch (result.state) {
    case "Printed":
    case "Completed":
      return { kind: "completed" };
    case "not-found":
      return { kind: "not-found" };
    case "denied":
      return { kind: "denied" };
    case "not-retryable":
      return { kind: "unavailable" };
    case "Ambiguous":
    case "Unknown":
    case "recovery-required":
      return { kind: "unknown" };
    case "Failed":
    default:
      return { kind: "failed" };
  }
}

type TrustedCartProductMatch = Readonly<{
  productCode: string;
  lookupCode: string;
  productImage: string | null;
}>;

async function resolveTrustedCartProductImage(
  input: Readonly<{
    productCode: string;
    lookupCode: string;
    apiBaseUrl: string;
    findExact(lookupCode: string): Promise<TrustedCartProductMatch | null>;
    findExactCandidates?(
      lookupCode: string,
    ): Promise<readonly TrustedCartProductMatch[]>;
  }>,
): Promise<string | null> {
  const productCode = normalizeCatalogIdentity(input.productCode);
  const lookupCode = normalizeCatalogIdentity(input.lookupCode);
  if (!productCode || !lookupCode) return null;

  // 一码多商品时购物车行或候选可能不是目录胜出项，需在同码候选中按商品编码取图。
  const match = input.findExactCandidates
    ? (await input.findExactCandidates(lookupCode)).find(
        (candidate) => candidate.productCode.trim() === productCode,
      ) ?? null
    : await input.findExact(lookupCode);
  if (
    !match ||
    match.productCode.trim() !== productCode ||
    match.lookupCode.trim() !== lookupCode
  ) {
    return null;
  }

  return resolveTrustedProductImageUri(match.productImage, input.apiBaseUrl);
}

function normalizeCatalogIdentity(value: string): string | null {
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > 256 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    return null;
  }
  return normalized;
}
