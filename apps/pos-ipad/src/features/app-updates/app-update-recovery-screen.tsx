import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  ScrollView,
  Text,
  View,
} from "react-native";

import {
  UPDATE_RECOVERY_SNAPSHOT_UNAVAILABLE,
  type AppUpdateRecoverySnapshot,
} from "./app-update-recovery-contract";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

export type AppUpdateRecoverySection = "settings" | "support";

export type AppUpdateRecoveryScreenState =
  | Readonly<{ kind: "loading" }>
  | Readonly<{
      kind: "error";
      errorCode: typeof UPDATE_RECOVERY_SNAPSHOT_UNAVAILABLE;
    }>
  | Readonly<{
      kind: "ready";
      snapshot: AppUpdateRecoverySnapshot;
    }>;

type AppUpdateRecoveryScreenProps = Readonly<{
  section: AppUpdateRecoverySection;
  state: AppUpdateRecoveryScreenState;
  exporting: boolean;
  exportError: boolean;
  onSelectSection(section: AppUpdateRecoverySection): void;
  onOpenRegistration(): void;
  onRetry(): void;
  onExport(): void;
}>;

const copy = {
  en: {
    eyebrow: "UPDATE RECOVERY",
    title: "Terminal settings and support",
    subtitle:
      "Read-only diagnostics stay available while sales pages are locked for a required update.",
    settings: "Settings",
    support: "Support",
    registration: "Device registration",
    retry: "Retry diagnostics",
    export: "Export diagnostics",
    exporting: "Exporting…",
    exportFailed: "Diagnostics export failed. Please try again.",
    unavailable: "Diagnostics are temporarily unavailable.",
    unavailableCode: "Error code",
    supportTitle: "Update support",
    detailsTitle: "Diagnostics",
    supportBody:
      "This export contains only version, connection and masked device details. It does not include credentials, orders, payments or audit records.",
    labels: {
      appVersion: "App version",
      buildNumber: "Build",
      runtimeVersion: "Runtime",
      channel: "OTA channel",
      apiOrigin: "API origin",
      backendState: "Backend",
      deviceState: "Device state",
    },
  },
  zh: {
    eyebrow: "更新恢复",
    title: "终端设置与支持",
    subtitle: "强制更新锁定业务页面期间，仍可查看只读诊断并处理设备注册。",
    settings: "设置",
    support: "支持",
    registration: "设备注册",
    retry: "重试诊断",
    export: "导出诊断",
    exporting: "正在导出…",
    exportFailed: "诊断导出失败，请重试。",
    unavailable: "诊断信息暂不可用。",
    unavailableCode: "错误代码",
    supportTitle: "更新支持",
    detailsTitle: "诊断信息",
    supportBody:
      "导出内容仅包含版本、连接状态和脱敏设备信息，不包含凭据、订单、支付或审计记录。",
    labels: {
      appVersion: "应用版本",
      buildNumber: "构建号",
      runtimeVersion: "Runtime",
      channel: "OTA Channel",
      apiOrigin: "API Origin",
      backendState: "后台状态",
      deviceState: "设备状态",
    },
  },
} as const;

const SNAPSHOT_FIELDS = [
  "appVersion",
  "buildNumber",
  "runtimeVersion",
  "channel",
  "apiOrigin",
  "backendState",
  "deviceState",
] as const satisfies readonly (keyof AppUpdateRecoverySnapshot)[];

const SECTION_ICONS: Record<AppUpdateRecoverySection, PosIconName> = {
  settings: "cog-outline",
  support: "lifebuoy",
};

// 后台 / 设备状态按好坏着色。只存色名，渲染时再按当前主题取色；没列出的状态用普通文字色。
const STATE_INK: Readonly<Record<string, keyof PosPalette>> = {
  reachable: "green",
  "authorized-online": "green",
  "authorized-local": "green",
  offline: "red",
  rejected: "red",
  locked: "red",
};
const STATE_FIELDS: ReadonlySet<string> = new Set(["backendState", "deviceState"]);

/**
 * 更新恢复页：强制更新锁住业务页面时仍可打开的只读页面。
 * 页头没有返回键（业务页面被锁，没有可返回的地方），右侧是「设备注册」；
 * 左侧两个分区按钮「设置 / 支持」，右侧是导出卡（仅「支持」分区）和 7 行只读诊断信息。
 * 这一页不显示左侧功能栏（rail="none"）。
 */
export function AppUpdateRecoveryScreen({
  section,
  state,
  exporting,
  exportError,
  onSelectSection,
  onOpenRegistration,
  onRetry,
  onExport,
}: AppUpdateRecoveryScreenProps) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const text = (i18n.resolvedLanguage ?? i18n.language)
    .toLowerCase()
    .startsWith("zh")
    ? copy.zh
    : copy.en;

  return (
    <PosScreenFrame
      header={
        <PosPageHeader
          actions={
            <PosButton
              icon="tablet"
              label={text.registration}
              onPress={onOpenRegistration}
              size="medium"
              sound="navigate"
              testID="app-update-recovery-registration"
            />
          }
          subtitle={text.subtitle}
          title={text.title}
        />
      }
      rail="none"
      testID="app-update-recovery-screen"
    >
      <View style={styles.workspace}>
        <View accessibilityRole="tablist" style={styles.navigation}>
          {(["settings", "support"] as const).map((item) => {
            const selected = section === item;
            // 当前分区用深色块填充，文字换成深色块上的浅色。
            const foreground = selected ? posColors.onInverse : posColors.ink;
            return (
              <PosPressable
                accessibilityLabel={text[item]}
                accessibilityRole="tab"
                accessibilityState={{ selected }}
                key={item}
                onPress={() => onSelectSection(item)}
                sound="navigate"
                style={({ pressed }) => [
                  styles.navigationButton,
                  selected && styles.navigationButtonSelected,
                  pressed && !selected && styles.pressed,
                ]}
                testID={`app-update-recovery-nav-${item}`}
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={foreground}
                  name={SECTION_ICONS[item]}
                  size={24}
                />
                <Text
                  numberOfLines={1}
                  style={[styles.navigationButtonText, { color: foreground }]}
                >
                  {text[item]}
                </Text>
              </PosPressable>
            );
          })}
        </View>

        <ScrollView
          contentContainerStyle={styles.content}
          showsVerticalScrollIndicator={false}
          style={styles.contentColumn}
        >
          {state.kind === "loading" ? (
            <View style={[styles.card, styles.centerState]}>
              <ActivityIndicator color={posColors.accent} size="large" />
            </View>
          ) : state.kind === "error" ? (
            <View style={styles.errorCard}>
              <Text style={styles.errorTitle}>{text.unavailable}</Text>
              <Text selectable style={styles.errorCode}>
                {text.unavailableCode}: {state.errorCode}
              </Text>
              <PosButton
                icon="refresh"
                label={text.retry}
                onPress={onRetry}
                size="medium"
                style={styles.retryButton}
                testID="app-update-recovery-retry"
                tone="danger"
              />
            </View>
          ) : (
            <>
              {section === "support" ? (
                <View style={[styles.card, styles.supportCard]}>
                  <View style={styles.supportCopy}>
                    <Text style={styles.cardTitle}>{text.supportTitle}</Text>
                    <Text style={styles.supportBody}>{text.supportBody}</Text>
                    {exportError ? (
                      <Text
                        accessibilityRole="alert"
                        style={styles.exportError}
                        testID="app-update-recovery-export-error"
                      >
                        {text.exportFailed}
                      </Text>
                    ) : null}
                  </View>
                  <PosButton
                    disabled={exporting}
                    icon="export-variant"
                    label={exporting ? text.exporting : text.export}
                    onPress={onExport}
                    style={styles.exportButton}
                    testID="app-update-recovery-export"
                  />
                </View>
              ) : null}
              <View style={[styles.card, styles.detailsCard]}>
                <Text style={[styles.cardTitle, styles.detailsTitle]}>
                  {text.detailsTitle}
                </Text>
                {SNAPSHOT_FIELDS.map((field) => {
                  const value = state.snapshot[field];
                  const ink = STATE_FIELDS.has(field)
                    ? (STATE_INK[value] ?? "ink")
                    : "ink";
                  return (
                    <View key={field} style={styles.detailRow}>
                      <Text style={styles.detailLabel}>{text.labels[field]}</Text>
                      <Text
                        selectable
                        style={[styles.detailValue, { color: posColors[ink] }]}
                        testID={`app-update-recovery-${field}`}
                      >
                        {value}
                      </Text>
                    </View>
                  );
                })}
              </View>
            </>
          )}
        </ScrollView>
      </View>
    </PosScreenFrame>
  );
}

const styles = createPosStyles((colors) => ({
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  navigation: { gap: 10, width: 218 },
  navigationButton: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 12,
    minHeight: 66,
    paddingHorizontal: 16,
  },
  navigationButtonSelected: {
    backgroundColor: colors.inverseSurface,
    borderColor: colors.inverseSurface,
  },
  navigationButtonText: { flexShrink: 1, fontSize: 17, fontWeight: "900" },
  pressed: { opacity: 0.72 },
  contentColumn: { flex: 1, minWidth: 0 },
  content: { flexGrow: 1, gap: 12 },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 16,
  },
  cardTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  centerState: {
    alignItems: "center",
    flexGrow: 1,
    justifyContent: "center",
    minHeight: 280,
  },
  supportCard: { alignItems: "center", flexDirection: "row", gap: 16 },
  supportCopy: { flex: 1, minWidth: 0 },
  supportBody: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 20,
    marginTop: 4,
  },
  exportButton: { flexShrink: 0, width: 210 },
  exportError: {
    color: colors.red,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 22,
    marginTop: 8,
  },
  detailsCard: { flexGrow: 1 },
  detailsTitle: { marginBottom: 6 },
  detailRow: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: 34,
    paddingVertical: 5,
  },
  detailLabel: {
    color: colors.mutedInk,
    flexShrink: 0,
    fontSize: 17,
    fontWeight: "700",
    lineHeight: 24,
  },
  detailValue: {
    flexShrink: 1,
    fontSize: 17,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    lineHeight: 24,
    textAlign: "right",
  },
  errorCard: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 16,
  },
  errorTitle: { color: colors.red, fontSize: 19, fontWeight: "900" },
  errorCode: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "600",
    marginTop: 6,
  },
  retryButton: { alignSelf: "flex-start", marginTop: 14 },
}));
