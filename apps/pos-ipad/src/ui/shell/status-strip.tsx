import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useTranslation } from "react-i18next";
import { Text, View } from "react-native";

import {
  describeBleScannerIndicator,
  isBleScannerReconnectable,
} from "./ble-scanner-indicator";
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

type ReconnectableIndicatorProps = StatusIndicatorProps &
  Readonly<{
    accessibilityHint: string;
    accessibilityLabel: string;
    onPress: () => void;
    testID: string;
    /** 末尾提示图标：重连用「↻」，打开配对面板用「›」。 */
    trailingIcon?: StatusIconName;
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

/** 顶栏右端版本号的最大宽度：超过就折成两行，避免长版本串挤掉状态胶囊。 */
export const POS_TOP_BAR_VERSION_MAX_WIDTH = 96;

/** 分店名再窄也保留的宽度（图标 + 一两个字）；更窄时由状态胶囊省略让位。 */
export const POS_TOP_BAR_STORE_MIN_WIDTH = 44;

/**
 * 状态图标 + 右上角色点。色点叠在图标角上而不是单独占一格，
 * 每个胶囊省出约 13pt；顶栏一行要放下 7 个胶囊，英文文案又长，宽度很紧。
 */
function StatusGlyph({
  icon,
  tone,
}: Readonly<{ icon: StatusIconName; tone: IndicatorTone }>) {
  return (
    <View style={styles.glyph}>
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.chromeMuted}
        name={icon}
        size={16}
      />
      <View
        style={[styles.dot, toneStyles[tone]]}
        testID={`status-strip-dot-${tone}`}
      />
    </View>
  );
}

function StatusIndicator({ icon, label, tone, value }: StatusIndicatorProps) {
  return (
    <View
      accessibilityLabel={`${label}: ${value}`}
      style={styles.indicator}
    >
      <StatusGlyph icon={icon} tone={tone} />
      <Text numberOfLines={1} style={styles.value}>
        {value}
      </Text>
    </View>
  );
}

/**
 * 可点按的状态胶囊：外观与 StatusIndicator 一致，末尾多一个「↻」提示可点。
 * 目前只用于蓝牙扫码枪休眠 / 断开 / 重连中时的「点一下重连」。
 */
function ReconnectableIndicator({
  accessibilityHint,
  accessibilityLabel,
  icon,
  onPress,
  testID,
  tone,
  trailingIcon = "refresh",
  value,
}: ReconnectableIndicatorProps) {
  return (
    <PosPressable
      accessibilityHint={accessibilityHint}
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      hitSlop={6}
      onPress={onPress}
      style={({ pressed }) => [
        styles.indicator,
        styles.indicatorPressable,
        pressed && styles.indicatorPressed,
      ]}
      testID={testID}
    >
      <StatusGlyph icon={icon} tone={tone} />
      <Text numberOfLines={1} style={styles.value}>
        {value}
      </Text>
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.chromeInk}
        name={trailingIcon}
        size={15}
      />
    </PosPressable>
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
    bleScanner,
    bleScannerOpenPairing,
    bleScannerReconnect,
    deviceGate,
    display,
    pendingSync,
    printer,
    scanner,
    terminalPresentation,
  } = usePosShellStore();
  // 没有 BLE 服务（旧原生包）或本机不支持蓝牙时为 null，不显示图标。
  const bleIndicator = bleScanner ? describeBleScannerIndicator(bleScanner) : null;
  // 已连接且读到电量时用专用文案：中文「已连接 85%」，英文只写「85%」
  // （蓝牙已连接图标 + 绿点已表达连上了，英文全拼太长会把其他胶囊挤出省略号）。
  const bleValue = bleIndicator
    ? bleIndicator.batteryLevel === null
      ? t(bleIndicator.labelKey)
      : t("status.bleScanner.readyWithBattery", {
          level: bleIndicator.batteryLevel,
        })
    : "";
  // 休眠 / 重连中 / 已断开时顶栏图标可点：点一下直接重连，不用进设置页。
  const bleReconnect =
    bleScanner && bleScannerReconnect && isBleScannerReconnectable(bleScanner)
      ? bleScannerReconnect
      : null;
  // 未配对且当前页面登记了配对面板（设备注册页）时，「未配对」可点：点一下打开配对面板。
  const blePairing =
    bleScanner && bleScannerOpenPairing && bleScanner.state === "not-paired"
      ? bleScannerOpenPairing
      : null;
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
      ) : (
        <View style={styles.spacer} />
      )}
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
      {bleIndicator && bleReconnect ? (
        <ReconnectableIndicator
          accessibilityHint={t("status.bleScanner.reconnectHint")}
          accessibilityLabel={t("status.bleScanner.reconnectLabel", { state: bleValue })}
          icon={bleIndicator.icon}
          label={t("status.bleScanner")}
          onPress={bleReconnect}
          testID="status-strip-ble-reconnect"
          tone={bleIndicator.tone}
          value={bleValue}
        />
      ) : bleIndicator && blePairing ? (
        <ReconnectableIndicator
          accessibilityHint={t("status.bleScanner.pairHint")}
          accessibilityLabel={t("status.bleScanner.pairLabel", { state: bleValue })}
          icon={bleIndicator.icon}
          label={t("status.bleScanner")}
          onPress={blePairing}
          testID="status-strip-ble-pair"
          tone={bleIndicator.tone}
          trailingIcon="chevron-right"
          value={bleValue}
        />
      ) : bleIndicator ? (
        <StatusIndicator
          icon={bleIndicator.icon}
          label={t("status.bleScanner")}
          tone={bleIndicator.tone}
          value={bleValue}
        />
      ) : null}
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
          // 版本号最多两行右对齐（「v0.2.0 (23) ·」一行、「OTA xxxxxxxx」一行），
          // 限宽后不再独占约 260pt，把宽度让给左边的状态胶囊。
          numberOfLines={2}
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
    // 设备代码短且定长，始终完整显示；与分店名之间共留 12pt（行间距 6 + 这里 6）。
    flexShrink: 0,
    marginLeft: 6,
  },
  deviceCodeIdentityValue: {
    flexShrink: 0,
  },
  dot: {
    borderColor: colors.chromeChip,
    borderRadius: 5,
    borderWidth: 1.5,
    height: 9,
    position: "absolute",
    right: -3,
    top: -2,
    width: 9,
  },
  glyph: {
    flexShrink: 0,
    height: 16,
    width: 16,
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
    gap: 5,
    minHeight: 32,
    minWidth: 0,
    paddingHorizontal: 8,
  },
  indicatorPressable: {
    borderColor: colors.chromeBorder,
    borderWidth: 1,
  },
  indicatorPressed: {
    opacity: 0.6,
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
    // 分店名不按内容定宽，只拿这一行其余各项排完后剩下的宽度（同时顶替弹性空白，把状态胶囊推到右侧），
    // 放不下就省略号收尾；剩余为负时由状态胶囊收缩。
    // 不能写成「按内容定宽 + flexShrink」：外层被压窄时 Yoga 不会回头压缩里面的文字，
    // 设备代码会溢出、盖住右侧的状态胶囊（英文界面实测）。
    flexBasis: 0,
    flexGrow: 1,
    flexShrink: 0,
    marginLeft: 8,
    minWidth: POS_TOP_BAR_STORE_MIN_WIDTH,
    overflow: "hidden",
  },
  storeIdentityValue: {
    flexShrink: 1,
    minWidth: 0,
  },
  terminalIdentity: {
    // 外框只用来分组（测试 / 无障碍定位），本身不参与布局：
    // 分店名、设备代码直接作为顶栏这一行的弹性子项分配宽度，避免嵌套收缩失效。
    display: "contents",
  },
  value: {
    color: colors.chromeInk,
    // 胶囊被压窄时文字跟着收缩并以省略号结尾；不设的话 Text 保持原宽，
    // 会溢出胶囊、压到相邻胶囊上（英文界面尤其明显）。
    flexShrink: 1,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    minWidth: 0,
  },
  version: {
    color: colors.chromeMuted,
    flexShrink: 0,
    fontSize: 10.5,
    fontVariant: ["tabular-nums"],
    lineHeight: 13,
    marginLeft: 6,
    maxWidth: POS_TOP_BAR_VERSION_MAX_WIDTH,
    textAlign: "right",
  },
}));
