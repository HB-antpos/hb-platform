import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import type { PrinterConnectionState, PrinterReconnectInfo } from "@/modules/printer/state";
import type { SavedPrinter } from "@/modules/printer/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface PrinterConnectionViewProps {
  savedPrinter: SavedPrinter | null;
  status: PrinterConnectionState;
  lastError: string | null;
}

interface PrinterConnectionChipProps extends PrinterConnectionViewProps {
  /** 打开打印设置（正常状态点按、需要重连时长按）。 */
  onPress?: () => void;
  /** 一键重新连接：只在「已断开 / 连接已暂停 / 连接异常」时生效——此时点按直接重连，不再先开设置。 */
  onReconnect?: () => void;
}

interface PrinterConnectionSummaryProps extends PrinterConnectionViewProps {
  onManage?: () => void;
  onReconnect?: () => void;
  /** 自动重连进度：用来告诉操作员「正在自动重试 / 多久重试一次」，避免以为 App 卡死。 */
  reconnectInfo?: PrinterReconnectInfo;
}

type EffectiveStatus = PrinterConnectionState | "unselected";

/** 需要用户介入的状态：这些状态下提供「重新连接」入口。 */
const RECONNECTABLE_STATUSES: ReadonlySet<EffectiveStatus> = new Set<EffectiveStatus>([
  "disconnected",
  "paused",
  "error",
]);

// 图标形状随状态变化，不单靠颜色区分；需要处理的状态（断开 / 暂停 / 异常）用带色底，
// 正常与过渡状态用中性或浅色底，操作员扫一眼就能分出"能不能打"。
const STATUS_PRESENTATION: Record<
  EffectiveStatus,
  { icon: string; color: string; background: string }
> = {
  unselected: { icon: "printer-off-outline", color: HB_COLORS.textSecondary, background: HB_COLORS.surfaceMuted },
  idle: { icon: "printer-outline", color: HB_COLORS.textSecondary, background: HB_COLORS.surfaceMuted },
  connecting: { icon: "bluetooth-connect", color: HB_COLORS.action, background: "#EAF2FF" },
  connected: { icon: "printer-check", color: HB_COLORS.success, background: "#ECFDF3" },
  reconnecting: { icon: "sync", color: HB_COLORS.warning, background: "#FFF4E5" },
  disconnected: { icon: "printer-off-outline", color: HB_COLORS.warning, background: "#FFF4E5" },
  paused: { icon: "pause-circle-outline", color: HB_COLORS.warning, background: "#FFF4E5" },
  error: { icon: "alert-circle-outline", color: HB_COLORS.danger, background: "#FEF3F2" },
};

/** chip 与弹窗摘要共用的状态解析：同一份状态、同一套文案，避免两处口径漂移。 */
function usePrinterConnectionView({ savedPrinter, status, lastError }: PrinterConnectionViewProps) {
  const { t } = useAppTranslation(["productQuery"]);
  const effectiveStatus: EffectiveStatus = savedPrinter ? status : "unselected";
  const printerName = savedPrinter?.name?.trim() || savedPrinter?.address || null;
  // 原生错误可能包含堆栈或驱动细节；这里只提示处理方向，避免把底层信息直接展示给操作员。
  const errorHint = effectiveStatus === "error" && lastError
    ? t("print.printerStatus.errorHint")
    : null;
  const statusLabel = t(`print.printerStatus.${effectiveStatus}`);

  return {
    t,
    effectiveStatus,
    presentation: STATUS_PRESENTATION[effectiveStatus],
    statusLabel,
    printerName,
    errorHint,
    // 朗读完整信息：状态 + 打印机名 + 处理提示。
    summary: [statusLabel, printerName, errorHint].filter(Boolean).join(" · "),
  };
}

/**
 * 打印行左侧的紧凑状态标识：只有「带色底的状态图标」，没有文字，把整行宽度让给四个打印按钮（Zebra 小屏）。
 * 状态靠颜色 + 图标形状双重区分（不单靠颜色）；完整状态、打印机名与异常提示仍通过朗读标签提供，
 * 并收进打印设置弹窗，点击图标即打开。
 */
export const PrinterConnectionChip = memo(function PrinterConnectionChip({
  savedPrinter,
  status,
  lastError,
  onPress,
  onReconnect,
}: PrinterConnectionChipProps) {
  const { t, effectiveStatus, presentation, summary } = usePrinterConnectionView({
    savedPrinter,
    status,
    lastError,
  });
  // 断开 / 暂停 / 异常时，点按直接重连（门店反馈：连不上时要连点好几层设置才能重连）；设置改为长按进入。
  const reconnectable = Boolean(onReconnect) && RECONNECTABLE_STATUSES.has(effectiveStatus);
  const handlePress = reconnectable ? onReconnect : onPress;

  return (
    <Pressable
      accessibilityRole={handlePress ? "button" : undefined}
      accessibilityLabel={summary}
      accessibilityHint={
        reconnectable
          ? t("print.printerStatus.reconnectHint")
          : onPress
            ? t("print.printerStatus.openSettingsHint")
            : undefined
      }
      // 状态变化（连接中 → 已连接 / 断开）要主动播报，操作员不一定盯着这个角落。
      accessibilityLiveRegion="polite"
      disabled={!handlePress}
      hitSlop={{ top: 8, bottom: 8, left: 4, right: 4 }}
      onPress={handlePress}
      onLongPress={reconnectable ? onPress : undefined}
      style={({ pressed }) => [
        styles.chip,
        { backgroundColor: presentation.background },
        pressed && handlePress ? styles.pressed : null,
      ]}
    >
      <Icon source={presentation.icon} size={20} color={presentation.color} />
    </Pressable>
  );
});

/** 打印设置弹窗顶部的"打印机"区块：完整状态、打印机名、异常提示，以及去打印机设置页的入口。 */
export const PrinterConnectionSummary = memo(function PrinterConnectionSummary({
  savedPrinter,
  status,
  lastError,
  onManage,
  onReconnect,
  reconnectInfo,
}: PrinterConnectionSummaryProps) {
  const { t, effectiveStatus, presentation, statusLabel, printerName, errorHint, summary } = usePrinterConnectionView({
    savedPrinter,
    status,
    lastError,
  });
  const reconnectable = Boolean(onReconnect) && RECONNECTABLE_STATUSES.has(effectiveStatus);
  // 自动重连进度只在没连上时显示：前 10 次显示次数，之后说明探测频率。
  const retryHint = effectiveStatus !== "connected" && reconnectInfo && reconnectInfo.phase !== "idle"
    ? reconnectInfo.phase === "backoff"
      ? t("print.printerStatus.retry.backoff", { count: reconnectInfo.failures })
      : reconnectInfo.phase === "probe_fast"
        ? t("print.printerStatus.retry.probeFast")
        : t("print.printerStatus.retry.probeSlow")
    : null;

  return (
    <View style={styles.summary} accessible accessibilityLabel={summary} accessibilityLiveRegion="polite">
      <View style={[styles.summaryIcon, { backgroundColor: presentation.background }]}>
        <Icon source={presentation.icon} size={20} color={presentation.color} />
      </View>
      <View style={styles.summaryCopy}>
        <Text variant="labelLarge" style={[styles.summaryStatus, { color: presentation.color }]} numberOfLines={1}>
          {statusLabel}
        </Text>
        {printerName ? (
          <Text variant="bodySmall" style={styles.summaryName} numberOfLines={1}>
            {printerName}
          </Text>
        ) : null}
        {errorHint ? (
          <Text variant="bodySmall" style={styles.summaryHint} numberOfLines={3}>
            {errorHint}
          </Text>
        ) : null}
        {retryHint ? (
          <Text variant="bodySmall" style={styles.summaryRetry} numberOfLines={2}>
            {retryHint}
          </Text>
        ) : null}
        {reconnectable ? (
          <Button compact mode="contained" icon="bluetooth-connect" onPress={onReconnect} style={styles.summaryReconnect}>
            {t("print.printerStatus.reconnect")}
          </Button>
        ) : null}
      </View>
      {onManage ? (
        <Button compact mode="outlined" onPress={onManage} style={styles.summaryAction}>
          {t("print.printerStatus.manage")}
        </Button>
      ) : null}
    </View>
  );
});

const styles = StyleSheet.create({
  chip: {
    flexShrink: 0,
    width: 36,
    height: 36,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
  },
  pressed: {
    opacity: 0.72,
  },
  summary: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingBottom: HB_SPACING.sm,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  summaryIcon: {
    width: 36,
    height: 36,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
  },
  summaryCopy: {
    flex: 1,
    minWidth: 0,
  },
  summaryStatus: {
    fontWeight: "700",
  },
  summaryName: {
    color: HB_COLORS.textSecondary,
  },
  summaryHint: {
    color: HB_COLORS.danger,
  },
  summaryRetry: {
    color: HB_COLORS.textSecondary,
  },
  summaryReconnect: {
    alignSelf: "flex-start",
    marginTop: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
  },
  summaryAction: {
    flexShrink: 0,
    borderRadius: HB_RADIUS.control,
  },
});
