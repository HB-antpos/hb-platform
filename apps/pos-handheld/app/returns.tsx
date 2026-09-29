import {
  Redirect,
  type Href,
  useLocalSearchParams,
  useRouter,
} from "expo-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  RETURN_MIN_TOUCH_TARGET,
  type ReturnPresenter,
  ReturnScreen,
} from "@/features/returns";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { RouteHidScannerCapture } from "@/ui/scanner/scanner-route-bridge";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { posColors } from "@/ui/theme";

type ReturnPresenterBinding = Readonly<{
  services: object;
  cashier: object;
  presenter: ReturnPresenter;
}>;

type ReturnRouteFailure = "unavailable" | "creation";

/**
 * 退货 route 只取得组合根公开的 returns facade。View 授权、SQLCipher、
 * provider 与恢复键全部留在 createPresenter 的生产闭包中。
 */
export default function ReturnsRoute() {
  const { dismissTo } = useRouter();
  const params = useLocalSearchParams<{
    orderRef?: string | string[];
  }>();
  const initialReceiptQuery = routeOrderRef(params.orderRef);
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const [binding, setBinding] =
    useState<ReturnPresenterBinding | null>(null);
  const [failure, setFailure] = useState<ReturnRouteFailure | null>(null);
  const [retryEpoch, setRetryEpoch] = useState(0);
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
      setFailure(null);
      return undefined;
    }

    const services = runtime.services;
    const cashier = activeCashier;
    const returns = services.returns;
    setBinding(null);
    if (returns.status !== "available") {
      setFailure("unavailable");
      return undefined;
    }

    let cancelled = false;
    let createdPresenter: ReturnPresenter | null = null;
    setFailure(null);
    // 通过 microtask 同时归一化 Promise rejection 与工厂同步抛错。
    void Promise.resolve().then(() => returns.createPresenter()).then(
      (nextPresenter) => {
        if (cancelled) {
          nextPresenter.destroy();
          return;
        }
        createdPresenter = nextPresenter;
        setBinding({
          services,
          cashier,
          presenter: nextPresenter,
        });
      },
      (error: unknown) => {
        if (cancelled) return;
        if (isViewPermissionDenied(error)) {
          // View 拒绝不是登录失效：保留 cashier，只退出退货工作区。
          dismissTo("/sales" as Href);
          return;
        }
        setFailure("creation");
      },
    );

    return () => {
      cancelled = true;
      createdPresenter?.destroy();
      createdPresenter = null;
    };
  }, [
    activeCashier,
    gate,
    retryEpoch,
    dismissTo,
    runtime.services,
  ]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login") {
    return <Redirect href={"/login" as Href} />;
  }
  if (failure) {
    return (
      <ReturnRouteError
        failure={failure}
        language={i18n.resolvedLanguage ?? i18n.language}
        onBack={() => dismissTo("/sales" as Href)}
        {...(failure === "creation"
          ? {
              onRetry: () => {
                setFailure(null);
                setRetryEpoch((value) => value + 1);
              },
            }
          : {})}
      />
    );
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  return (
    <ReturnScreen
      {...(initialReceiptQuery ? { initialReceiptQuery } : {})}
      onBack={() => dismissTo("/sales" as Href)}
      presenter={presenter}
      renderHidScannerCapture={({ enabled, onScan }) => (
        // 与销售页同一套隐藏捕获：查询区无焦点时也能接收 DataWedge / HID 扫码。
        <RouteHidScannerCapture
          context="product-search"
          enabled={enabled}
          onScan={onScan}
          path="/returns"
        />
      )}
    />
  );
}

function routeOrderRef(value: string | string[] | undefined): string | null {
  const candidate = Array.isArray(value) ? value[0] : value;
  if (typeof candidate !== "string") return null;
  const normalized = candidate.trim();
  return normalized.length > 0 && normalized.length <= 128
    ? normalized
    : null;
}

function ReturnRouteError({
  failure,
  language,
  onBack,
  onRetry,
}: Readonly<{
  failure: ReturnRouteFailure;
  language: string;
  onBack(): void;
  onRetry?: () => void;
}>) {
  const chinese = language.toLowerCase().startsWith("zh");
  const unavailable = failure === "unavailable";
  return (
    <SafeAreaView style={styles.safeArea} testID="returns-route-error">
      <View accessibilityRole="alert" style={styles.errorPanel}>
        <Text style={styles.eyebrow}>
          {chinese ? "退货工作区" : "RETURNS WORKSPACE"}
        </Text>
        <Text style={styles.title}>
          {unavailable
            ? chinese
              ? "退货服务暂不可用"
              : "Returns are unavailable"
            : chinese
              ? "无法打开退货"
              : "Returns could not be opened"}
        </Text>
        <Text style={styles.hint}>
          {unavailable
            ? chinese
              ? "当前终端尚未配置完整的退货与主管授权服务。"
              : "This terminal is not configured with the required returns and supervisor services."
            : chinese
              ? "本次退货初始化失败。可重试，或返回销售继续收银。"
              : "Returns initialization failed. Retry, or return to sales to keep trading."}
        </Text>
        <View style={styles.actions}>
          {onRetry ? (
            <RouteButton
              label={chinese ? "重试" : "Retry"}
              onPress={onRetry}
              testID="returns-route-retry"
            />
          ) : null}
          <RouteButton
            label={chinese ? "返回销售" : "Back to sales"}
            onPress={onBack}
            secondary={Boolean(onRetry)}
            sound="navigate"
            testID="returns-route-back"
          />
        </View>
      </View>
    </SafeAreaView>
  );
}

function RouteButton({
  label,
  onPress,
  secondary = false,
  sound = "tap",
  testID,
}: Readonly<{
  label: string;
  onPress(): void;
  secondary?: boolean;
  sound?: "tap" | "navigate";
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      onPress={onPress}
      sound={sound}
      style={({ pressed }) => [
        styles.button,
        secondary && styles.buttonSecondary,
        pressed && styles.buttonPressed,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.buttonText,
          secondary && styles.buttonSecondaryText,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function isViewPermissionDenied(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    (error as Readonly<{ code?: unknown }>).code ===
      "RETURN_VIEW_FORBIDDEN"
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: "row",
    gap: 10,
    marginTop: 8,
  },
  button: {
    alignItems: "center",
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
    borderRadius: 4,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 128,
    paddingHorizontal: 18,
  },
  buttonPressed: {
    opacity: 0.72,
  },
  buttonSecondary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
  },
  buttonSecondaryText: {
    color: posColors.ink,
  },
  buttonText: {
    color: "#FFFFFF",
    fontSize: 15,
    fontWeight: "800",
  },
  errorPanel: {
    alignItems: "flex-start",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    gap: 12,
    maxWidth: 560,
    padding: 28,
    width: "100%",
  },
  eyebrow: {
    color: posColors.orange,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1,
  },
  hint: {
    color: posColors.mutedInk,
    fontSize: 16,
    lineHeight: 24,
    maxWidth: 480,
  },
  safeArea: {
    alignItems: "center",
    backgroundColor: posColors.canvas,
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  title: {
    color: posColors.ink,
    fontSize: 26,
    fontWeight: "900",
  },
});
