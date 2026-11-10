import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, ScrollView, Text, useWindowDimensions, View } from "react-native";

import {
  loadExpoBootstrapServerDiagnostics,
  type BootstrapServerDiagnostics,
} from "@/core/runtime/expo-bootstrap-server-diagnostics";
import { loadExpoStartupOtaRecovery } from "@/core/runtime/expo-startup-ota-recovery";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { StartupOtaRecoveryPanel } from "@/features/app-updates/startup-ota-recovery-panel";
import { serverConnectionPanelCopy } from "@/features/device-registration/server-connection-copy";
import { ServerConnectionPanel } from "@/features/device-registration/server-connection-panel";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosKeyboardAwareScrollView } from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import {
  createPosStyles,
  posColors,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

/**
 * 状态卡的四种样子：
 * good 已就绪；danger 出问题（整张红底红描边）；pending 启动中还没轮到；idle 可选项未接入（例如没接客显）。
 */
type ReadinessTone = "good" | "danger" | "pending" | "idle";

type ReadinessCardProps = {
  icon: PosIconName;
  label: string;
  tone: ReadinessTone;
  statusText?: string;
  testID?: string;
};

// 只存色名，渲染时再取 posColors[name]，换主题后颜色才会跟着变。
const TONE_COLOR: Record<ReadinessTone, keyof PosPalette> = {
  good: "green",
  danger: "red",
  pending: "blue",
  idle: "mutedInk",
};

const TONE_MARK: Record<ReadinessTone, PosIconName> = {
  good: "check-circle",
  danger: "alert-circle",
  pending: "progress-clock",
  idle: "minus-circle-outline",
};

function ReadinessCard({
  icon,
  label,
  statusText,
  testID,
  tone,
}: ReadinessCardProps) {
  usePosThemeVersion();
  const { t } = useTranslation();
  const color = posColors[TONE_COLOR[tone]];

  return (
    <View
      style={[styles.card, tone === "danger" && styles.cardDanger]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={color}
        name={icon}
        size={40}
      />
      <View style={styles.cardCopy}>
        <Text numberOfLines={1} style={styles.cardLabel}>
          {label}
        </Text>
        <Text numberOfLines={2} style={[styles.cardState, { color }]}>
          {statusText ??
            (tone === "good" ? t("bootstrap.ready") : t("bootstrap.pending"))}
        </Text>
      </View>
      <MaterialCommunityIcons
        accessible={false}
        color={color}
        name={TONE_MARK[tone]}
        size={30}
      />
    </View>
  );
}

/**
 * 核心启动项（设备 / 服务端 / 本机账本）的状态卡色调：
 * 已就绪为绿；明确失败，或启动已经停在失败而它还没就绪，整张标红；其余是启动中的等待态。
 */
function coreTone(
  ready: boolean,
  hardFailure: boolean,
  startupFailed: boolean,
): ReadinessTone {
  if (ready) return "good";
  return hardFailure || startupFailed ? "danger" : "pending";
}

export function BootstrapScreen({
  onOpenNetworkCheck,
}: Readonly<{
  /** 启动失败时显示「网络检测」入口；只有首页路由传入，其他路由的加载兜底页不显示。 */
  onOpenNetworkCheck?: () => void;
}> = {}) {
  usePosThemeVersion();
  const { t } = useTranslation();
  const { width } = useWindowDimensions();
  const { retry, state: runtime } = usePosRuntime();
  const [serverDiagnostics, setServerDiagnostics] =
    useState<BootstrapServerDiagnostics | null>(null);
  const [abandoningPendingActivation, setAbandoningPendingActivation] =
    useState(false);
  const [retryingStartup, setRetryingStartup] = useState(false);
  const [pendingActivationAbandoned, setPendingActivationAbandoned] =
    useState(false);
  const [pendingActivationError, setPendingActivationError] = useState<
    string | null
  >(null);
  const serverProbe = useRef<AbortController | null>(null);
  const startupActionInFlight = useRef(false);
  const lastLoggedError = useRef<string | null>(null);
  const display = usePosShellStore((state) => state.display);
  const compact = width < 900;
  const backendReady = runtime.backend === "reachable";
  const databaseReady = runtime.database === "ready";
  const deviceReady =
    runtime.device === "authorized-local" ||
    runtime.device === "authorized-online";
  const canAbandonPendingActivation =
    runtime.phase === "failed" &&
    serverDiagnostics?.canAbandonPendingDeviceActivation === true &&
    !pendingActivationAbandoned;
  const startupFailed = runtime.phase === "failed";
  const deviceTone = coreTone(
    deviceReady,
    runtime.device === "locked",
    startupFailed,
  );
  const backendTone = coreTone(
    backendReady,
    runtime.backend === "rejected",
    startupFailed,
  );
  const databaseTone = coreTone(
    databaseReady,
    runtime.database === "failed",
    startupFailed,
  );
  // 客显是可选外设：没接不算问题，只有明确报错才标红。
  const displayTone: ReadinessTone =
    display === "ready" ? "good" : display === "failed" ? "danger" : "idle";
  const [otaRecoveryBusy, setOtaRecoveryBusy] = useState(false);
  const startupActionBusy =
    abandoningPendingActivation || retryingStartup || otaRecoveryBusy;
  // reload 前读取最新相位：只有 runtime 仍失败（SQLite 已在失败收尾关闭）且未在重试/放弃激活时才允许套用 OTA。
  const runtimePhase = useRef(runtime.phase);
  runtimePhase.current = runtime.phase;
  const loadOtaRecovery = useCallback(
    () =>
      loadExpoStartupOtaRecovery({
        canReload: () =>
          runtimePhase.current === "failed" &&
          !startupActionInFlight.current,
      }),
    [],
  );

  const retryStartup = async () => {
    if (startupActionInFlight.current || otaRecoveryBusy) return;
    startupActionInFlight.current = true;
    setRetryingStartup(true);
    try {
      await retry();
    } catch {
      // runtime controller 已保存新的失败原因，启动页继续显示该状态。
    } finally {
      startupActionInFlight.current = false;
      setRetryingStartup(false);
    }
  };

  const confirmAbandonPendingActivation = () => {
    if (startupActionInFlight.current) return;
    Alert.alert(
      t("bootstrap.abandonPendingTitle"),
      t("bootstrap.abandonPendingMessage"),
      [
        {
          style: "cancel",
          text: t("bootstrap.abandonPendingCancel"),
        },
        {
          onPress: () => {
            void (async () => {
              if (
                startupActionInFlight.current ||
                !serverDiagnostics?.canAbandonPendingDeviceActivation
              ) {
                return;
              }
              startupActionInFlight.current = true;
              setAbandoningPendingActivation(true);
              setPendingActivationError(null);
              try {
                await serverDiagnostics.abandonPendingDeviceActivation();
                setPendingActivationAbandoned(true);
                await retry().catch(() => undefined);
              } catch {
                setPendingActivationError(t("bootstrap.abandonPendingFailed"));
              } finally {
                startupActionInFlight.current = false;
                setAbandoningPendingActivation(false);
              }
            })();
          },
          style: "destructive",
          text: t("bootstrap.abandonPendingConfirm"),
        },
      ],
    );
  };

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
      "[HBPOS][iPad][Bootstrap] Runtime initialization failed.",
      runtime.error,
    );
  }, [runtime.error, runtime.phase]);

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

  const statusMessage =
    pendingActivationError ??
    (startupFailed
      ? bootstrapErrorSummary(runtime.error, t)
      : t("bootstrap.footer"));
  const statusIsError = startupFailed || pendingActivationError !== null;

  return (
    // 启动阶段不允许跳去其他页面，这一页不显示左侧栏。
    <PosScreenFrame rail="none">
      <StatusBar style="light" />
      <View style={[styles.page, compact && styles.pageCompact]}>
        {/* 左栏：标题、四张状态卡、启动结果提示；失败时底部多一条只读的服务器连接。 */}
        {/* 服务器地址输入框贴在这一栏底部，用键盘感知滚动容器，软键盘弹出时能把它让出来。 */}
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.mainContent}
          showsVerticalScrollIndicator={false}
          style={styles.mainColumn}
        >
          <View>
            <Text style={styles.eyebrow}>{t("bootstrap.eyebrow")}</Text>
            <Text accessibilityRole="header" style={styles.title}>
              {t("bootstrap.title")}
            </Text>
            <Text style={styles.subtitle}>{t("bootstrap.subtitle")}</Text>
          </View>

          <View style={styles.cardGrid}>
            <View style={styles.cardRow}>
              <ReadinessCard
                icon="tablet"
                label={t("bootstrap.device")}
                statusText={t(`bootstrap.deviceState.${runtime.device}`)}
                testID="bootstrap-card-device"
                tone={deviceTone}
              />
              <ReadinessCard
                icon={
                  backendTone === "danger"
                    ? "server-network-off"
                    : "server-network"
                }
                label={t("bootstrap.backend")}
                statusText={t(`bootstrap.backendState.${runtime.backend}`)}
                testID="bootstrap-card-backend"
                tone={backendTone}
              />
            </View>
            <View style={styles.cardRow}>
              <ReadinessCard
                icon="cash-lock"
                label={t("bootstrap.offline")}
                statusText={t(`bootstrap.databaseState.${runtime.database}`)}
                testID="bootstrap-card-database"
                tone={databaseTone}
              />
              <ReadinessCard
                icon="monitor"
                label={t("bootstrap.display")}
                statusText={t(`status.peripheral.${display}`)}
                testID="bootstrap-card-display"
                tone={displayTone}
              />
            </View>
          </View>

          {statusIsError ? (
            <View accessibilityRole="alert" style={styles.errorBanner}>
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.red}
                name="alert-outline"
                size={24}
              />
              <Text style={styles.errorBannerText}>{statusMessage}</Text>
            </View>
          ) : (
            <View style={styles.footer}>
              <View style={styles.footerDot} />
              <Text style={styles.footerText}>{statusMessage}</Text>
            </View>
          )}

          {startupFailed && serverDiagnostics ? (
            <View style={styles.serverDiagnostics}>
              {/* 准备阶段只读：canSave 固定为 false，只能改候选地址并测试，不能保存。 */}
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
        </PosKeyboardAwareScrollView>

        {/* 右栏只在启动失败时出现：上面是「修复更新」，下面三个按钮固定在底部，始终点得到。 */}
        {startupFailed ? (
          <View
            style={[styles.actionColumn, compact && styles.actionColumnCompact]}
          >
            <ScrollView
              contentContainerStyle={styles.recoveryContent}
              showsVerticalScrollIndicator={false}
              style={styles.recoveryScroll}
            >
              <StartupOtaRecoveryPanel
                load={loadOtaRecovery}
                onBusyChange={setOtaRecoveryBusy}
              />
            </ScrollView>
            {canAbandonPendingActivation ? (
              <PosButton
                disabled={startupActionBusy}
                label={
                  abandoningPendingActivation
                    ? t("bootstrap.abandoningPending")
                    : t("bootstrap.abandonPending")
                }
                onPress={confirmAbandonPendingActivation}
                sound="tap"
                testID="bootstrap-abandon-pending-activation"
                tone="danger"
              />
            ) : null}
            {onOpenNetworkCheck ? (
              <PosButton
                icon="access-point-network"
                label={t("bootstrap.networkCheck")}
                onPress={onOpenNetworkCheck}
                sound="navigate"
                testID="bootstrap-network-check"
              />
            ) : null}
            <PosButton
              disabled={startupActionBusy}
              icon="restart"
              label={t("bootstrap.retry")}
              loading={retryingStartup}
              onPress={() => {
                void retryStartup();
              }}
              size="primary"
              sound="tap"
              testID="bootstrap-retry"
              tone="primary"
            />
          </View>
        ) : null}
      </View>
    </PosScreenFrame>
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

const styles = createPosStyles((colors) => ({
  page: {
    flex: 1,
    flexDirection: "row",
    gap: 18,
    minHeight: 0,
    paddingHorizontal: 18,
    paddingVertical: 14,
  },
  // 窄屏（分屏等）时左右两栏改为上下排，按钮仍固定在下方。
  pageCompact: {
    flexDirection: "column",
    gap: 12,
    paddingHorizontal: 12,
    paddingVertical: 12,
  },
  mainColumn: {
    flex: 1,
    minWidth: 0,
  },
  mainContent: {
    flexGrow: 1,
    gap: 14,
  },
  eyebrow: {
    color: colors.accent,
    fontSize: 13,
    fontWeight: "900",
    letterSpacing: 2,
  },
  title: {
    color: colors.ink,
    fontSize: 30,
    fontWeight: "900",
    marginTop: 4,
  },
  subtitle: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    marginTop: 4,
  },
  cardGrid: {
    gap: 12,
  },
  cardRow: {
    flexDirection: "row",
    gap: 12,
  },
  card: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 14,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    gap: 14,
    minHeight: 92,
    minWidth: 0,
    paddingHorizontal: 18,
    paddingVertical: 16,
  },
  cardDanger: {
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
  },
  cardCopy: {
    flex: 1,
    minWidth: 0,
  },
  cardLabel: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  cardState: {
    fontSize: 14,
    fontWeight: "800",
    marginTop: 3,
  },
  errorBanner: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  errorBannerText: {
    color: colors.red,
    flex: 1,
    fontSize: 16,
    fontWeight: "700",
    lineHeight: 22,
  },
  footer: {
    alignItems: "center",
    flexDirection: "row",
    gap: 9,
  },
  footerDot: {
    backgroundColor: colors.accent,
    borderRadius: 4,
    height: 8,
    width: 8,
  },
  footerText: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
  },
  serverDiagnostics: {
    // 内容不满一屏时贴底；超出一屏时 auto 间距归零，靠 mainContent 的 gap 留白。
    marginTop: "auto",
  },
  actionColumn: {
    gap: 12,
    width: 360,
  },
  actionColumnCompact: {
    maxHeight: "55%",
    width: "100%",
  },
  recoveryScroll: {
    flex: 1,
    minHeight: 0,
  },
  recoveryContent: {
    flexGrow: 1,
  },
}));
