import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useTranslation } from "react-i18next";
import { Text, View } from "react-native";

import { usePosShellStore } from "./pos-shell-store";

import { PosPressable } from "@/ui/controls/pos-pressable";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

type IndicatorTone = "good" | "warning" | "neutral" | "danger";
type StatusIconName = keyof typeof MaterialCommunityIcons.glyphMap;

type StatusIndicatorProps = {
  icon: StatusIconName;
  label: string;
  value: string;
  tone: IndicatorTone;
};

type PosStatusStripProps = Readonly<{
  language?: string;
  onSwitchLanguage?: () => void;
  showTerminalIdentity?: boolean;
  /** 顶栏最右侧的小字版本号；不传则不显示。 */
  appVersionLabel?: string | null;
  /** 在最左侧显示品牌名「HB POS」。 */
  showBrand?: boolean;
}>;

type StaticIdentityProps = Readonly<{
  icon: StatusIconName;
  label: string;
  testID: string;
  truncate?: boolean;
  value: string | null | undefined;
}>;

/** 顶栏高度：页面布局与弹窗定位按它留位。 */
export const POS_TOP_BAR_HEIGHT = 52;

function StatusIndicator({ icon, label, tone, value }: StatusIndicatorProps) {
  return (
    <View
      accessibilityLabel={`${label}: ${value}`}
      style={styles.indicator}
    >
      <View style={[styles.dot, toneStyles[tone]]} />
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.chromeMuted}
        name={icon}
        size={16}
      />
      <Text numberOfLines={1} style={styles.value}>
        {value}
      </Text>
    </View>
  );
}

function StaticIdentity({
  icon,
  label,
  testID,
  truncate = false,
  value,
}: StaticIdentityProps) {
  const displayValue = value?.trim() || "—";

  return (
    <View
      accessibilityLabel={`${label}: ${displayValue}`}
      style={[
        styles.staticIdentity,
        truncate
          ? styles.storeIdentity
          : styles.deviceCodeIdentity,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.chromeMuted}
        name={icon}
        size={15}
      />
      <Text
        numberOfLines={1}
        style={[
          styles.identityValue,
          truncate
            ? styles.storeIdentityValue
            : styles.deviceCodeIdentityValue,
        ]}
      >
        {displayValue}
      </Text>
    </View>
  );
}

/**
 * 顶栏：品牌、门店与设备身份、各项设备状态、语言切换。
 * 所有页面共用同一条，状态一眼可见，不再占用单独一行。
 */
export function PosStatusStrip({
  appVersionLabel,
  language,
  onSwitchLanguage,
  showBrand = false,
  showTerminalIdentity = false,
}: PosStatusStripProps = {}) {
  usePosThemeVersion();
  const { i18n, t } = useTranslation();
  const {
    connectivity,
    deviceGate,
    display,
    pendingSync,
    printer,
    scanner,
    terminalPresentation,
  } = usePosShellStore();
  const currentLanguage =
    language ?? i18n.resolvedLanguage ?? i18n.language;
  const targetLanguageGlyph = currentLanguage
    .toLowerCase()
    .startsWith("zh")
    ? "EN"
    : "中";

  return (
    <View style={styles.container}>
      {showBrand ? (
        <Text
          accessible={false}
          numberOfLines={1}
          style={styles.brand}
          testID="status-strip-brand"
        >
          HB POS
        </Text>
      ) : null}
      <StatusIndicator
        icon="tablet"
        label={t("status.device")}
        tone={deviceGate === "authorized" ? "good" : deviceGate === "locked" ? "danger" : "warning"}
        value={t(`status.device.${deviceGate}`)}
      />
      {showTerminalIdentity ? (
        <View
          style={styles.terminalIdentity}
          testID="status-strip-terminal-identity"
        >
          <StaticIdentity
            icon="store-outline"
            label={t("status.storeName")}
            testID="status-strip-store-identity"
            truncate
            value={terminalPresentation?.storeName}
          />
          <StaticIdentity
            icon="identifier"
            label={t("status.deviceCode")}
            testID="status-strip-device-code-identity"
            value={terminalPresentation?.deviceCode}
          />
        </View>
      ) : null}
      <View style={styles.spacer} />
      <StatusIndicator
        icon="wifi"
        label={t("status.network")}
        tone={connectivity === "online" ? "good" : connectivity === "offline" ? "danger" : "neutral"}
        value={t(`status.network.${connectivity}`)}
      />
      <StatusIndicator
        icon="sync"
        label={t("status.sync")}
        tone={
          pendingSync.kind === "ready"
            ? pendingSync.count === 0
              ? "good"
              : "warning"
            : pendingSync.kind === "unavailable"
              ? "danger"
              : "neutral"
        }
        value={
          pendingSync.kind === "ready"
            ? t("status.sync.pending", { count: pendingSync.count })
            : t(`status.sync.${pendingSync.kind}`)
        }
      />
      <StatusIndicator
        icon="printer-outline"
        label={t("status.printer")}
        tone={printer === "ready" ? "good" : printer === "failed" ? "danger" : "neutral"}
        value={t(`status.peripheral.${printer}`)}
      />
      <StatusIndicator
        icon="barcode-scan"
        label={t("status.scanner")}
        tone={scanner === "capturing" || scanner === "camera" ? "good" : "neutral"}
        value={t(`status.scanner.${scanner}`)}
      />
      <StatusIndicator
        icon="monitor"
        label={t("status.display")}
        tone={display === "ready" ? "good" : display === "failed" ? "danger" : "neutral"}
        value={t(`status.peripheral.${display}`)}
      />
      {onSwitchLanguage ? (
        <PosPressable
          accessibilityHint={t("status.languageSwitchHint")}
          accessibilityLabel={t("status.languageSwitchLabel")}
          accessibilityRole="button"
          hitSlop={4}
          onPress={onSwitchLanguage}
          sound="navigate"
          style={({ pressed }) => [
            styles.languageButton,
            pressed && styles.languageButtonPressed,
          ]}
          testID="status-strip-language-switch"
        >
          <Text
            accessible={false}
            style={styles.languageTargetGlyph}
            testID="status-strip-language-icon"
          >
            {targetLanguageGlyph}
          </Text>
        </PosPressable>
      ) : null}
      {appVersionLabel ? (
        <Text
          numberOfLines={1}
          selectable={false}
          style={styles.version}
          testID="status-strip-app-version"
        >
          {appVersionLabel}
        </Text>
      ) : null}
    </View>
  );
}

const toneStyles = createPosStyles((colors) => ({
  danger: {
    backgroundColor: colors.red,
  },
  good: {
    backgroundColor: colors.green,
  },
  neutral: {
    backgroundColor: colors.chromeMuted,
  },
  warning: {
    backgroundColor: colors.yellow,
  },
}));

const styles = createPosStyles((colors) => ({
  brand: {
    color: colors.chromeInk,
    flexShrink: 0,
    fontSize: 17,
    fontWeight: "900",
    letterSpacing: 0.3,
    marginRight: 6,
  },
  container: {
    alignItems: "center",
    backgroundColor: colors.chrome,
    flexDirection: "row",
    gap: 6,
    minHeight: POS_TOP_BAR_HEIGHT,
    paddingHorizontal: 16,
  },
  deviceCodeIdentity: {
    flexShrink: 0,
  },
  deviceCodeIdentityValue: {
    flexShrink: 0,
  },
  dot: {
    borderRadius: 4,
    height: 8,
    width: 8,
  },
  identityValue: {
    color: colors.chromeInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  indicator: {
    alignItems: "center",
    backgroundColor: colors.chromeChip,
    borderRadius: 16,
    flexDirection: "row",
    flexShrink: 1,
    gap: 6,
    minHeight: 32,
    minWidth: 0,
    paddingHorizontal: 10,
  },
  languageButton: {
    alignItems: "center",
    borderColor: colors.chromeBorder,
    borderRadius: 8,
    borderWidth: 1.5,
    flexShrink: 0,
    justifyContent: "center",
    marginLeft: 6,
    minHeight: 44,
    minWidth: 52,
  },
  languageButtonPressed: {
    backgroundColor: colors.chromeChip,
  },
  languageTargetGlyph: {
    color: colors.chromeInk,
    fontSize: 14,
    fontWeight: "900",
    lineHeight: 17,
  },
  spacer: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
  },
  staticIdentity: {
    alignItems: "center",
    flexDirection: "row",
    gap: 4,
    minWidth: 0,
  },
  storeIdentity: {
    flexShrink: 1,
  },
  storeIdentityValue: {
    flexShrink: 1,
    minWidth: 0,
  },
  terminalIdentity: {
    alignItems: "center",
    flexDirection: "row",
    flexShrink: 1,
    gap: 12,
    marginLeft: 8,
    minWidth: 0,
  },
  value: {
    color: colors.chromeInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  version: {
    color: colors.chromeMuted,
    flexShrink: 0,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
    marginLeft: 6,
  },
}));
