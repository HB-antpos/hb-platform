import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Text,
  View,
} from "react-native";

import type {
  StartupOtaRecovery,
  StartupOtaRecoveryState,
} from "./startup-ota-recovery";

import { PosButton } from "@/ui/controls/pos-button";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

type StartupOtaRecoveryPanelProps = Readonly<{
  load(): Promise<StartupOtaRecovery>;
  /** 检查或安装进行中时通知启动页禁用"重试"，避免 runtime 重启与 channel 覆盖交叉。 */
  onBusyChange?(busy: boolean): void;
}>;

type PanelState =
  | StartupOtaRecoveryState
  | Readonly<{ phase: "load-failed" }>;

/**
 * 启动失败页的修复更新入口：自动做一次只读策略检查，安装必须由店员点击确认。
 * 构建未启用 OTA 时整块不渲染，保持开发包启动页不变。
 */
export function StartupOtaRecoveryPanel({
  load,
  onBusyChange,
}: StartupOtaRecoveryPanelProps) {
  usePosThemeVersion();
  const { t } = useTranslation();
  const [recovery, setRecovery] = useState<StartupOtaRecovery | null>(null);
  const [state, setState] = useState<PanelState>({ phase: "idle" });
  const onBusyChangeRef = useRef(onBusyChange);
  onBusyChangeRef.current = onBusyChange;

  useEffect(() => {
    let active = true;
    let unsubscribe: (() => void) | null = null;
    void load()
      .then((loaded) => {
        if (!active) return;
        setRecovery(loaded);
        unsubscribe = loaded.subscribe((next) => {
          if (active) setState(next);
        });
        void loaded.check();
      })
      .catch(() => {
        if (active) setState({ phase: "load-failed" });
      });
    return () => {
      active = false;
      unsubscribe?.();
    };
  }, [load]);

  const busy = state.phase === "checking" || state.phase === "applying";
  useEffect(() => {
    onBusyChangeRef.current?.(busy);
  }, [busy]);
  useEffect(
    () => () => {
      onBusyChangeRef.current?.(false);
    },
    [],
  );

  if (state.phase === "idle") return null;
  if (state.phase === "unavailable" && state.reason === "updates-disabled") {
    return null;
  }

  const recheck = () => {
    if (recovery) void recovery.check();
  };

  return (
    <View
      accessibilityLabel={t("bootstrap.otaRecovery.title")}
      style={styles.panel}
      testID="startup-ota-recovery"
    >
      <Text style={styles.eyebrow}>{t("bootstrap.otaRecovery.eyebrow")}</Text>
      <Text style={styles.title}>{t("bootstrap.otaRecovery.title")}</Text>
      {busy ? (
        <View style={styles.statusRow}>
          <ActivityIndicator color={posColors.blue} />
          <Text style={styles.body}>
            {state.phase === "applying"
              ? t("bootstrap.otaRecovery.applying")
              : t("bootstrap.otaRecovery.checking")}
          </Text>
        </View>
      ) : null}
      {state.phase === "available" ? (
        <>
          <Text style={styles.highlight}>
            {state.requirement === "required"
              ? t("bootstrap.otaRecovery.availableRequired")
              : t("bootstrap.otaRecovery.availableOptional")}
          </Text>
          {state.releaseMessage ? (
            <Text style={styles.body}>{state.releaseMessage}</Text>
          ) : null}
          <Text style={styles.body}>
            {t("bootstrap.otaRecovery.keepsData")}
          </Text>
          <PosButton
            icon="download"
            label={t("bootstrap.otaRecovery.install")}
            onPress={() => {
              void recovery?.apply();
            }}
            size="medium"
            sound="tap"
            style={styles.action}
            testID="startup-ota-recovery-apply"
            tone="primary"
          />
        </>
      ) : null}
      {state.phase === "up-to-date" ||
      state.phase === "unavailable" ||
      state.phase === "failed" ||
      state.phase === "load-failed" ? (
        <>
          <Text style={styles.body}>{panelMessage(state, t)}</Text>
          {state.phase !== "unavailable" && recovery ? (
            <PosButton
              icon="refresh"
              label={t("bootstrap.otaRecovery.check")}
              onPress={recheck}
              size="medium"
              sound="tap"
              style={styles.action}
              testID="startup-ota-recovery-check"
            />
          ) : null}
        </>
      ) : null}
    </View>
  );
}

function panelMessage(
  state: PanelState,
  t: ReturnType<typeof useTranslation>["t"],
): string {
  switch (state.phase) {
    case "up-to-date":
      return t("bootstrap.otaRecovery.upToDate");
    case "unavailable":
      return t("bootstrap.otaRecovery.deviceUnavailable");
    case "failed":
      if (state.reason === "selection-changed") {
        return t("bootstrap.otaRecovery.selectionChanged");
      }
      if (
        state.reason === "runtime-mismatch" ||
        state.reason === "update-id-mismatch" ||
        state.reason === "manifest-invalid"
      ) {
        return t("bootstrap.otaRecovery.mismatch");
      }
      if (state.reason === "not-available" || state.reason === "not-new") {
        return t("bootstrap.otaRecovery.notAvailable");
      }
      return t("bootstrap.otaRecovery.failed");
    default:
      return t("bootstrap.otaRecovery.failed");
  }
}

const styles = createPosStyles((colors) => ({
  panel: {
    gap: 8,
    padding: 16,
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderLeftColor: colors.accent,
    borderLeftWidth: 4,
    borderRadius: posRadius.large,
    borderWidth: 1,
  },
  eyebrow: {
    color: colors.accent,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  title: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  statusRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  highlight: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  body: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 20,
  },
  // 两个操作按钮都是 56 高，靠左按内容宽度排，不拉满整块面板。
  action: {
    alignSelf: "flex-start",
    marginTop: 6,
    minWidth: 180,
  },
}));
