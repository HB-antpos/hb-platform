import { MaterialCommunityIcons } from "@expo/vector-icons";
import { type Href, usePathname, useRouter } from "expo-router";
import {
  useCallback,
  useEffect,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import {
  StyleSheet,
  Text,
  View,
} from "react-native";

import {
  resolveAppUpdateCopy,
  type AppUpdateCopyKey,
} from "./app-update-copy";
import type {
  AppUpdateOrchestrator,
  AppUpdatePresentation,
} from "./app-update-orchestrator";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

const HIDDEN_PRESENTATION: AppUpdatePresentation = Object.freeze({
  key: "runtime-unavailable",
  kind: "none",
  requirement: null,
  phase: "hidden",
  blocking: false,
  releaseMessage: null,
  appStoreUrl: null,
});

const RECOVERY_ACCESS_PATHS = new Set([
  "/registration",
  "/update-recovery",
]);

/**
 * 更新恢复页的右上角是页头的「设备注册」和导出卡的「导出诊断」，小卡片放在右上角会把它们盖住，
 * 所以在这一页把更新入口停靠到右下角（诊断信息卡下方的空白处），横向排成一条。
 * 注册页仍是右上角小卡片。
 */
const RECOVERY_DOCK_PATH = "/update-recovery";

export function AppUpdateGateBridge() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const pathname = usePathname();
  const router = useRouter();
  const updates = runtime.services?.appUpdates ?? null;
  const presentation = useUpdatePresentation(updates);
  const copy = resolveAppUpdateCopy(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const [dismissedKey, setDismissedKey] = useState<string | null>(null);
  const [working, setWorking] = useState(false);
  const [errorKey, setErrorKey] =
    useState<AppUpdateCopyKey | null>(null);

  useEffect(() => {
    if (!updates || presentation.phase !== "waiting-for-safe") {
      return undefined;
    }
    const check = () => {
      void updates.refreshSafety().catch(() => {
        // 未能证明交易安全时继续保留恢复页面，下一轮再核验。
      });
    };
    const timer = setInterval(check, 1_000);
    return () => clearInterval(timer);
  }, [presentation.key, presentation.phase, updates]);

  useEffect(() => {
    setWorking(false);
    setErrorKey(null);
  }, [presentation.key]);

  const performUpdate = useCallback(async () => {
    if (!updates || working) return;
    setWorking(true);
    setErrorKey(null);
    try {
      const result = await updates.performSelectedUpdate();
      if (result.action === "open-app-store") {
        return;
      }
      if (result.action === "blocked") {
        setErrorKey("error.notSafe");
        return;
      }
      if (
        result.action !== "ota" ||
        result.result.state !== "reloaded"
      ) {
        setErrorKey("error.unavailable");
      }
    } catch {
      setErrorKey("error.unavailable");
    } finally {
      setWorking(false);
    }
  }, [updates, working]);

  if (
    presentation.phase === "hidden" ||
    presentation.phase === "unchecked" ||
    presentation.phase === "waiting-for-safe"
  ) {
    return null;
  }
  if (
    presentation.requirement === "optional" &&
    dismissedKey === presentation.key
  ) {
    return null;
  }

  const required = presentation.requirement === "required";
  const normalizedPath = normalizePathname(pathname);
  const preserveRecoveryAccess =
    required && RECOVERY_ACCESS_PATHS.has(normalizedPath);
  const docked =
    preserveRecoveryAccess && normalizedPath === RECOVERY_DOCK_PATH;
  const blocking = required && !preserveRecoveryAccess;
  const titleKey: AppUpdateCopyKey =
    presentation.kind === "native"
      ? required
        ? "required.nativeTitle"
        : "optional.nativeTitle"
      : required
        ? "required.otaTitle"
        : "optional.otaTitle";
  const actionKey: AppUpdateCopyKey =
    presentation.kind === "native"
      ? "action.openStore"
      : "action.installOta";
  const actionIcon: PosIconName =
    presentation.kind === "native" ? "apple" : "download";

  const message = (
    <>
      <Text style={styles.eyebrow}>
        {copy[required ? "eyebrow.required" : "eyebrow.optional"]}
      </Text>
      <Text style={[styles.title, !blocking && styles.titleCompact]}>
        {copy[titleKey]}
      </Text>
      <Text
        // 停靠条高度有限：说明最多三行，避免盖住下面的诊断信息。
        numberOfLines={docked ? 3 : undefined}
        style={[styles.body, !blocking && styles.bodyCompact]}
      >
        {presentation.releaseMessage ??
          copy[required ? "required.body" : "optional.body"]}
      </Text>
      {errorKey ? (
        <Text accessibilityRole="alert" style={styles.error}>
          {copy[errorKey]}
        </Text>
      ) : null}
    </>
  );

  // 主按钮：这一层里唯一的主操作色按钮。执行中禁用并换成「正在更新…」。
  const primaryAction = (
    <PosButton
      disabled={working}
      icon={actionIcon}
      label={copy[working ? "action.working" : actionKey]}
      onPress={performUpdate}
      size={blocking || docked ? "large" : "button"}
      style={
        docked
          ? styles.dockedAction
          : blocking
            ? styles.blockingAction
            : styles.fillAction
      }
      testID="app-update-action"
      tone="primary"
    />
  );

  return (
    <View
      accessibilityViewIsModal={blocking}
      pointerEvents={
        preserveRecoveryAccess
          ? "box-none"
          : required
            ? "auto"
            : "box-none"
      }
      style={
        docked
          ? styles.dockedLayer
          : preserveRecoveryAccess
            ? styles.recoveryAccessLayer
            : required
              ? styles.blockingLayer
              : styles.optionalLayer
      }
      testID={
        preserveRecoveryAccess
          ? "app-update-recovery-access"
          : required
          ? "app-update-blocking-gate"
          : "app-update-optional-prompt"
      }
    >
      {docked ? (
        <View accessibilityRole="alert" style={styles.dockedCard}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.accent}
            name="update"
            size={40}
          />
          <View style={styles.dockedCopy}>{message}</View>
          {primaryAction}
        </View>
      ) : (
        <View
          accessibilityRole="alert"
          style={[
            styles.card,
            preserveRecoveryAccess
              ? styles.recoveryAccessCard
              : required
                ? styles.requiredCard
                : styles.optionalCard,
          ]}
        >
          <View style={styles.accent} />
          {message}
          {blocking ? (
            // 强制更新：三个只读入口排成一行，主按钮单独占一行放在最下面。
            <View style={styles.blockingActions}>
              <View style={styles.entryRow}>
                <PosButton
                  fill
                  icon="cog-outline"
                  label={copy["action.settings"]}
                  onPress={() =>
                    router.push(
                      "/update-recovery?section=settings" as Href,
                    )
                  }
                  sound="navigate"
                  testID="app-update-settings-entry"
                />
                <PosButton
                  fill
                  icon="lifebuoy"
                  label={copy["action.support"]}
                  onPress={() =>
                    router.push(
                      "/update-recovery?section=support" as Href,
                    )
                  }
                  sound="navigate"
                  testID="app-update-support-entry"
                />
                <PosButton
                  fill
                  icon="tablet"
                  label={copy["action.registration"]}
                  onPress={() =>
                    router.push("/registration" as Href)
                  }
                  sound="navigate"
                  testID="app-update-registration-entry"
                />
              </View>
              {primaryAction}
            </View>
          ) : (
            <View style={styles.actions}>
              {!required ? (
                <PosButton
                  label={copy["action.later"]}
                  onPress={() => setDismissedKey(presentation.key)}
                  sound="navigate"
                  style={styles.laterAction}
                  testID="app-update-dismiss"
                  tone="quiet"
                />
              ) : null}
              {primaryAction}
            </View>
          )}
        </View>
      )}
    </View>
  );
}

function normalizePathname(pathname: string): string {
  const normalized = pathname.replace(/\/+$/u, "");
  return normalized || "/";
}

function useUpdatePresentation(
  updates: AppUpdateOrchestrator | null,
): AppUpdatePresentation {
  const subscribe = useCallback(
    (listener: () => void) =>
      updates?.subscribePresentation(listener) ?? (() => undefined),
    [updates],
  );
  const getSnapshot = useCallback(
    () => updates?.getPresentation() ?? HIDDEN_PRESENTATION,
    [updates],
  );
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

const styles = createPosStyles((colors) => ({
  blockingLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    backgroundColor: colors.canvas,
    elevation: 50,
    justifyContent: "center",
    paddingHorizontal: 48,
    zIndex: 1_000,
  },
  optionalLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "flex-end",
    justifyContent: "flex-start",
    paddingHorizontal: 28,
    paddingTop: 24,
    zIndex: 900,
  },
  recoveryAccessLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "flex-end",
    justifyContent: "flex-start",
    paddingHorizontal: 28,
    paddingTop: 24,
    zIndex: 900,
  },
  dockedLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "flex-end",
    justifyContent: "flex-end",
    // 底部多留一些，避开 iPad 底部的安全区，让停靠条落在诊断信息卡的空白处。
    paddingBottom: 40,
    paddingHorizontal: 24,
    zIndex: 900,
  },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    elevation: 12,
    maxWidth: 640,
    overflow: "hidden",
    paddingBottom: 20,
    paddingHorizontal: 24,
    paddingTop: 25,
    shadowColor: colors.shadow,
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.16,
    shadowRadius: 20,
    width: "100%",
  },
  requiredCard: {
    borderColor: colors.accent,
    borderWidth: 2,
    justifyContent: "center",
    // 加宽到 720：一行并排三个 64 高的入口按钮，英文「Device registration」也放得下。
    maxWidth: 720,
    minHeight: 320,
  },
  optionalCard: {
    maxWidth: 480,
  },
  recoveryAccessCard: {
    maxWidth: 420,
    minHeight: 0,
  },
  dockedCard: {
    alignItems: "center",
    backgroundColor: colors.accentSoft,
    borderColor: colors.accent,
    borderRadius: posRadius.large,
    borderWidth: 2,
    elevation: 12,
    flexDirection: "row",
    gap: 16,
    maxWidth: 760,
    padding: 14,
    shadowColor: colors.shadow,
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.16,
    shadowRadius: 20,
    width: "100%",
  },
  dockedCopy: { flex: 1, minWidth: 0 },
  accent: {
    backgroundColor: colors.accent,
    height: 5,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  eyebrow: {
    color: colors.accent,
    fontSize: 13,
    fontWeight: "900",
    letterSpacing: 1,
    marginBottom: 4,
  },
  title: {
    color: colors.ink,
    fontSize: 28,
    fontWeight: "900",
    lineHeight: 34,
  },
  titleCompact: { fontSize: 21, lineHeight: 27 },
  body: {
    color: colors.mutedInk,
    fontSize: 17,
    fontWeight: "600",
    lineHeight: 25,
    marginTop: 10,
  },
  bodyCompact: { fontSize: 14, lineHeight: 20, marginTop: 4 },
  error: {
    backgroundColor: colors.redSoft,
    borderRadius: posRadius.medium,
    color: colors.red,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
    marginTop: 12,
    overflow: "hidden",
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  actions: {
    flexDirection: "row",
    gap: 10,
    marginTop: 18,
  },
  blockingActions: {
    gap: 12,
    marginTop: 24,
  },
  entryRow: {
    flexDirection: "row",
    gap: 10,
  },
  laterAction: { minWidth: 120 },
  fillAction: { flex: 1 },
  blockingAction: { alignSelf: "stretch" },
  dockedAction: { flexShrink: 0, width: 240 },
}));
