import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import type { PrinterConnectionState } from "@/modules/printer/state";
import type { SavedPrinter } from "@/modules/printer/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface PrinterConnectionViewProps {
  savedPrinter: SavedPrinter | null;
  status: PrinterConnectionState;
  lastError: string | null;
}

interface PrinterConnectionChipProps extends PrinterConnectionViewProps {
  onPress?: () => void;
}

interface PrinterConnectionSummaryProps extends PrinterConnectionViewProps {
  onManage?: () => void;
}

type EffectiveStatus = PrinterConnectionState | "unselected";

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
    presentation: STATUS_PRESENTATION[effectiveStatus],
    statusLabel,
    shortLabel: t(`print.printerStatus.short.${effectiveStatus}`),
    printerName,
    errorHint,
    // 朗读完整信息：状态 + 打印机名 + 处理提示。
    summary: [statusLabel, printerName, errorHint].filter(Boolean).join(" · "),
  };
}

/**
 * 打印行左侧的紧凑状态标识：图标 + 2~4 字状态词，不额外占一行高度。
 * 打印机名与异常提示收进打印设置弹窗，点击 chip 即打开。
 */
export const PrinterConnectionChip = memo(function PrinterConnectionChip({
  savedPrinter,
  status,
  lastError,
  onPress,
}: PrinterConnectionChipProps) {
  const { t, presentation, shortLabel, summary } = usePrinterConnectionView({
    savedPrinter,
    status,
    lastError,
  });

  return (
    <Pressable
      accessibilityRole={onPress ? "button" : undefined}
      accessibilityLabel={summary}
      accessibilityHint={onPress ? t("print.printerStatus.openSettingsHint") : undefined}
      // 状态变化（连接中 → 已连接 / 断开）要主动播报，操作员不一定盯着这个角落。
      accessibilityLiveRegion="polite"
      disabled={!onPress}
      hitSlop={{ top: 8, bottom: 8, left: 4, right: 4 }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.chip,
        { backgroundColor: presentation.background },
        pressed && onPress ? styles.pressed : null,
      ]}
    >
      <Icon source={presentation.icon} size={16} color={presentation.color} />
      <Text style={[styles.chipLabel, { color: presentation.color }]} numberOfLines={1}>
        {shortLabel}
      </Text>
    </Pressable>
  );
});

/** 打印设置弹窗顶部的"打印机"区块：完整状态、打印机名、异常提示，以及去打印机设置页的入口。 */
export const PrinterConnectionSummary = memo(function PrinterConnectionSummary({
  savedPrinter,
  status,
  lastError,
  onManage,
}: PrinterConnectionSummaryProps) {
  const { t, presentation, statusLabel, printerName, errorHint, summary } = usePrinterConnectionView({
    savedPrinter,
    status,
    lastError,
  });

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
          <Text variant="bodySmall" style={styles.summaryHint} numberOfLines={2}>
            {errorHint}
          </Text>
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
    minHeight: 36,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
  },
  pressed: {
    opacity: 0.72,
  },
  chipLabel: {
    fontSize: 12,
    fontWeight: "700",
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
  summaryAction: {
    flexShrink: 0,
    borderRadius: HB_RADIUS.control,
  },
});
