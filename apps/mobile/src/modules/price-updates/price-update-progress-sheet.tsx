import { StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, ProgressBar, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HQ_ACTIONS_ENABLED } from "@/shared/utils/legacy-databases";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { summarizePrintProgress, type PriceUpdatePrintItem, type PriceUpdateRunState } from "./batch-runner";

interface PriceUpdateProgressSheetProps {
  state: PriceUpdateRunState | null;
  /** 单条重试进行中：禁用其它重试与完成按钮。 */
  retrying: boolean;
  stopRequested: boolean;
  onStop: () => void;
  onRetry: (taskId: number) => void;
  onDone: () => void;
}

function SummaryRow({
  icon,
  label,
  value,
  tone = HB_COLORS.textPrimary,
  loading = false,
}: {
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  label: string;
  value: string;
  tone?: string;
  loading?: boolean;
}) {
  return (
    <View style={styles.summaryRow}>
      <MaterialCommunityIcons name={icon} size={20} color={HB_COLORS.action} />
      <Text variant="bodyMedium" style={styles.flex}>{label}</Text>
      {loading ? <ActivityIndicator size={16} /> : null}
      <Text variant="bodyMedium" style={[styles.summaryValue, { color: tone }]}>{value}</Text>
    </View>
  );
}

function PrintRow({
  item,
  disabled,
  onRetry,
}: {
  item: PriceUpdatePrintItem;
  disabled: boolean;
  onRetry: (taskId: number) => void;
}) {
  const { t } = useAppTranslation("priceUpdates");
  const tone =
    item.status === "printed"
      ? HB_COLORS.success
      : item.status === "failed"
        ? HB_COLORS.danger
        : HB_COLORS.textSecondary;
  const statusLabel =
    item.status === "failed" && item.printedAwaitingConfirm
      ? t("progress.status.confirmFailed")
      : t(`progress.status.${item.status}`);
  const statusText = item.status === "failed" && item.error ? `${statusLabel} · ${item.error}` : statusLabel;

  return (
    <View style={styles.printRow}>
      <View style={styles.printText}>
        <Text variant="bodyMedium" numberOfLines={1}>
          {item.task.productName || item.task.productCode}
        </Text>
        <Text variant="bodySmall" style={{ color: tone }} numberOfLines={2}>
          {statusText}
        </Text>
      </View>
      {item.status === "printing" ? <ActivityIndicator size={16} /> : null}
      {item.status === "failed" || item.status === "skipped" ? (
        <Button compact mode="text" disabled={disabled} onPress={() => onRetry(item.taskId)}>
          {t("progress.retry")}
        </Button>
      ) : null}
    </View>
  );
}

export function PriceUpdateProgressSheet({
  state,
  retrying,
  stopRequested,
  onStop,
  onRetry,
  onDone,
}: PriceUpdateProgressSheetProps) {
  const { t } = useAppTranslation("priceUpdates");
  if (!state) {
    return null;
  }

  const running = state.phase !== "done";
  const progress = summarizePrintProgress(state);
  const applyValue =
    state.phase === "applying"
      ? t("progress.applying")
      : state.apply.failed > 0
        ? t("progress.applyResultWithFailed", { success: state.apply.success, failed: state.apply.failed })
        : t("progress.applyResult", { success: state.apply.success });

  return (
    <BusinessSheet
      visible
      title={t(state.mode === "applyAndPrint" ? "progress.titlePrint" : "progress.titleApply")}
      onDismiss={onDone}
      // 执行中不允许关闭：关闭后无法看到哪些标签失败，也无法停止打印。
      dismissable={!running && !retrying}
      footer={(
        <View style={styles.footer}>
          {running && state.phase === "printing" ? (
            <Button mode="outlined" disabled={stopRequested} onPress={onStop} style={styles.footerButton}>
              {t(stopRequested ? "progress.stopping" : "progress.stop")}
            </Button>
          ) : null}
          <Button mode="contained" disabled={running || retrying} onPress={onDone} style={styles.footerButton}>
            {t("progress.done")}
          </Button>
        </View>
      )}
    >
      {state.apply.total > 0 ? (
        <SummaryRow
          icon="tag-arrow-up-outline"
          label={t("progress.applyRow", { count: state.apply.total })}
          value={applyValue}
          tone={state.apply.failed > 0 ? HB_COLORS.warning : HB_COLORS.success}
          loading={state.phase === "applying"}
        />
      ) : null}
      {state.apply.error ? (
        <Text variant="bodySmall" style={styles.errorText}>{state.apply.error}</Text>
      ) : null}
      {state.apply.targetChanged > 0 ? (
        <Text variant="bodySmall" style={styles.warningText}>
          {t("progress.targetChanged", { count: state.apply.targetChanged })}
        </Text>
      ) : null}
      {HQ_ACTIONS_ENABLED && state.hqSyncEnabled && state.apply.total > 0 && state.phase !== "applying" ? (
        <SummaryRow
          icon="cloud-sync-outline"
          label={t("progress.hqSyncRow")}
          value={t("progress.hqSyncSubmitted", { count: state.hqSyncSubmittedCount })}
        />
      ) : null}

      {state.printerUnavailable ? (
        <Text variant="bodySmall" style={styles.warningText}>{t("progress.printerUnavailable")}</Text>
      ) : null}

      {state.mode === "applyAndPrint" && !state.printerUnavailable && state.phase !== "applying" ? (
        <View style={styles.printSection}>
          <View style={styles.summaryRow}>
            <MaterialCommunityIcons name="printer-outline" size={20} color={HB_COLORS.action} />
            <Text variant="bodyMedium" style={styles.flex}>{t("progress.printRow")}</Text>
            <Text variant="bodyMedium" style={styles.summaryValue}>
              {`${progress.printed}/${progress.total}`}
            </Text>
          </View>
          <ProgressBar
            progress={progress.total > 0 ? progress.processed / progress.total : 0}
            color={progress.failed > 0 ? HB_COLORS.warning : HB_COLORS.brand}
            style={styles.progressBar}
          />
          {progress.total === 0 ? (
            <Text variant="bodySmall" style={styles.secondary}>{t("progress.nothingToPrint")}</Text>
          ) : null}
          {state.prints.map((item) => (
            <PrintRow key={item.taskId} item={item} disabled={running || retrying} onRetry={onRetry} />
          ))}
          <Text variant="bodySmall" style={styles.secondary}>{t("progress.failedHint")}</Text>
        </View>
      ) : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  summaryRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 32 },
  summaryValue: { fontVariant: ["tabular-nums"], fontWeight: "700" },
  printSection: { gap: HB_SPACING.xs },
  progressBar: { height: 6, borderRadius: 3, backgroundColor: HB_COLORS.surfaceMuted },
  printRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 48, paddingHorizontal: HB_SPACING.xs, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  printText: { flex: 1, minWidth: 0 },
  secondary: { color: HB_COLORS.textSecondary },
  warningText: { color: HB_COLORS.warning },
  errorText: { color: HB_COLORS.danger },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
});
