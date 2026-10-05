import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  loadExpoBootstrapServerDiagnostics,
  type BootstrapServerDiagnostics,
} from "@/core/runtime/expo-bootstrap-server-diagnostics";
import { loadExpoStartupOtaRecovery } from "@/core/runtime/expo-startup-ota-recovery";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { StartupOtaRecoveryPanel } from "@/features/app-updates/startup-ota-recovery-panel";
import { serverConnectionPanelCopy } from "@/features/device-registration/server-connection-copy";
import { ServerConnectionPanel } from "@/features/device-registration/server-connection-panel";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldStateSurface } from "@/ui/handheld";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type ReadinessCardProps = {
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  label: string;
  ready: boolean;
  statusText?: string;
};

function ReadinessCard({
  icon,
  label,
  ready,
  statusText,
}: ReadinessCardProps) {
  const { t } = useTranslation();

  return (
    <View style={styles.card}>
      <View style={[styles.iconFrame, ready ? styles.iconReady : styles.iconPending]}>
        <MaterialCommunityIcons
          color={ready ? posColors.green : posColors.blue}
          name={icon}
          size={28}
        />
      </View>
      <View style={styles.cardCopy}>
        <Text style={styles.cardLabel}>{label}</Text>
        <Text style={[styles.cardState, ready ? styles.stateReady : styles.statePending]}>
          {statusText ?? (ready ? t("bootstrap.ready") : t("bootstrap.pending"))}
        </Text>
      </View>
    </View>
  );
}

export function BootstrapScreen() {
  const { t } = useTranslation();
  const { retry, state: runtime } = usePosRuntime();
  const [serverDiagnostics, setServerDiagnostics] =
    useState<BootstrapServerDiagnostics | null>(null);
  const serverProbe = useRef<AbortController | null>(null);
  const lastLoggedError = useRef<string | null>(null);
  const [otaRecoveryBusy, setOtaRecoveryBusy] = useState(false);
  // reload 前读取最新相位：只有 runtime 仍失败（SQLite 已在失败收尾关闭）才允许套用 OTA。
  const runtimePhase = useRef(runtime.phase);
  runtimePhase.current = runtime.phase;
  const loadOtaRecovery = useCallback(
    () =>
      loadExpoStartupOtaRecovery({
        canReload: () => runtimePhase.current === "failed",
      }),
    [],
  );
  const backendReady = runtime.backend === "reachable";
  const databaseReady = runtime.database === "ready";
  const deviceReady =
    runtime.device === "authorized-local" ||
    runtime.device === "authorized-online";

  useEffect(() => {
    let active = true;
    void loadExpoBootstrapServerDiagnostics()
      .then((diagnostics) => {
        if (active) setServerDiagnostics(diagnostics);
      })
      .catch(() => {
        // 公开配置自身损坏时仍保留原始启动错误与重试入口。
      });
    return () => {
      active = false;
      serverProbe.current?.abort();
    };
  }, []);

  useEffect(() => {
    if (
      runtime.phase !== "failed" ||
      !runtime.error ||
      lastLoggedError.current === runtime.error
    ) {
      return;
    }
    lastLoggedError.current = runtime.error;
    // 原始初始化异常只进入开发/设备日志；操作员界面使用稳定的本地化摘要。
    console.error(
      "[HBPOS][Handheld][Bootstrap] Runtime initialization failed.",
      runtime.error,
    );
  }, [runtime.error, runtime.phase]);

  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar style="dark" />
      <PosStatusStrip />
      <HandheldStateSurface slug="startup" style={styles.stateSurface}>
        <ScrollView
          contentContainerStyle={styles.page}
          keyboardShouldPersistTaps="handled"
          showsVerticalScrollIndicator={false}
        >
          <View style={styles.brandRail}>
            <View>
              <Text style={styles.brandName}>{t("app.name")}</Text>
              <Text style={styles.eyebrow}>{t("bootstrap.eyebrow")}</Text>
            </View>
          </View>

          <View style={styles.hero}>
            <View style={styles.orangeRule} />
            <Text style={styles.title}>{t("bootstrap.title")}</Text>
            <Text style={styles.subtitle}>{t("bootstrap.subtitle")}</Text>
          </View>

          <View style={styles.cardGrid}>
            <ReadinessCard
              icon="table-account"
              label={t("bootstrap.device")}
              ready={deviceReady}
              statusText={t(`bootstrap.deviceState.${runtime.device}`)}
            />
            <ReadinessCard
              icon="server-security"
              label={t("bootstrap.backend")}
              ready={backendReady}
              statusText={t(`bootstrap.backendState.${runtime.backend}`)}
            />
            <ReadinessCard
              icon="database-lock"
              label={t("bootstrap.offline")}
              ready={databaseReady}
              statusText={t(`bootstrap.databaseState.${runtime.database}`)}
            />
          </View>

          {runtime.phase === "failed" && serverDiagnostics ? (
            <View style={styles.serverDiagnostics}>
              <ServerConnectionPanel
                canSave={false}
                copy={serverConnectionPanelCopy(t)}
                currentAddress={serverDiagnostics.currentApiBaseUrl}
                saveAddress={() =>
                  Promise.reject(
                    new Error("BOOTSTRAP_SERVER_SWITCH_SAFETY_UNAVAILABLE"),
                  )
                }
                testAddress={(address) => {
                  const controller = new AbortController();
                  serverProbe.current?.abort();
                  serverProbe.current = controller;
                  return serverDiagnostics.test(address, controller.signal);
                }}
              />
            </View>
          ) : null}

          {runtime.phase === "failed" ? (
            <StartupOtaRecoveryPanel
              load={loadOtaRecovery}
              onBusyChange={setOtaRecoveryBusy}
            />
          ) : null}

          <View style={styles.footer}>
            <View style={styles.footerDot} />
            <View style={styles.footerCopy}>
              <Text style={styles.footerText}>
                {runtime.phase === "failed"
                  ? bootstrapErrorSummary(runtime.error, t)
                  : t("bootstrap.footer")}
              </Text>
              {runtime.phase === "failed" ? (
                <PosPressable
                  accessibilityRole="button"
                  accessibilityState={{ disabled: otaRecoveryBusy }}
                  disabled={otaRecoveryBusy}
                  onPress={() => {
                    void retry().catch(() => undefined);
                  }}
                  sound="tap"
                  style={({ pressed }) => [
                    styles.retryButton,
                    pressed && styles.retryButtonPressed,
                  ]}
                >
                  <Text style={styles.retryLabel}>{t("bootstrap.retry")}</Text>
                </PosPressable>
              ) : null}
            </View>
          </View>
        </ScrollView>
      </HandheldStateSurface>
    </SafeAreaView>
  );
}

function bootstrapErrorSummary(
  error: string | undefined,
  t: ReturnType<typeof useTranslation>["t"],
): string {
  if (
    error &&
    /secure\s*store|securestore|keychain|entitlement|keychain-access-groups/iu.test(
      error,
    )
  ) {
    return t("bootstrap.error.secureStorage");
  }
  return t("bootstrap.error.generic");
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  stateSurface: { flex: 1 },
  page: {
    flexGrow: 1,
    padding: 16,
  },
  brandRail: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  brandName: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "800",
  },
  eyebrow: {
    marginTop: 2,
    color: posColors.orange,
    fontSize: 10,
    fontWeight: "800",
    letterSpacing: 1.6,
  },
  hero: {
    marginTop: 24,
  },
  orangeRule: {
    width: 48,
    height: 4,
    marginBottom: 16,
    backgroundColor: posColors.orange,
  },
  title: {
    color: posColors.ink,
    fontSize: 28,
    fontWeight: "800",
    letterSpacing: -1.2,
    lineHeight: 34,
  },
  subtitle: {
    marginTop: 8,
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
  },
  cardGrid: {
    gap: 8,
    marginTop: 24,
  },
  serverDiagnostics: {
    marginTop: 20,
    width: "100%",
  },
  card: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    minHeight: 64,
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  iconFrame: {
    width: 40,
    height: 40,
    alignItems: "center",
    justifyContent: "center",
  },
  iconReady: {
    backgroundColor: posColors.greenSoft,
  },
  iconPending: {
    backgroundColor: posColors.blueSoft,
  },
  cardCopy: {
    flex: 1,
  },
  cardLabel: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  cardState: {
    marginTop: 4,
    fontSize: 12,
    fontWeight: "700",
  },
  stateReady: {
    color: posColors.green,
  },
  statePending: {
    color: posColors.blue,
  },
  footer: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 9,
    marginTop: 24,
  },
  footerCopy: {
    flex: 1,
    gap: 8,
  },
  footerDot: {
    width: 7,
    height: 7,
    borderRadius: 4,
    backgroundColor: posColors.orange,
  },
  footerText: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "600",
    letterSpacing: 0.4,
  },
  retryButton: {
    minHeight: 48,
    width: "100%",
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 18,
    backgroundColor: posColors.ink,
  },
  retryButtonPressed: {
    opacity: 0.78,
  },
  retryLabel: {
    color: "#FFFFFF",
    fontSize: 13,
    fontWeight: "800",
  },
});
