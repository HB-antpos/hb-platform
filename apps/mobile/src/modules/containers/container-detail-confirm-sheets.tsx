import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { CreateNewProductsResultView, ResultLine, ResultTone } from "./container-detail-job-results";
import { formatContainerDetailNumber } from "./container-detail-table-columns";
import type { MissingRetailPriceDetail } from "./types";

/** 确认 → 执行中 → 结果 三段式；执行中不可关闭，避免用户误以为已取消。 */
export type JobSheetPhase = "confirm" | "running" | "result";

/** 缺价清单最多展示的行数，其余折叠为「另有 N 个」（定位后补价，清单会自然缩短）。 */
const MISSING_PRICE_DISPLAY_LIMIT = 30;

const TONE_COLOR: Record<ResultTone, string> = {
  success: HB_COLORS.success,
  warning: HB_COLORS.warning,
  danger: HB_COLORS.danger,
  neutral: HB_COLORS.textSecondary,
};

const TONE_ICON: Record<ResultTone, "check-circle" | "alert" | "close-circle" | "information"> = {
  success: "check-circle",
  warning: "alert",
  danger: "close-circle",
  neutral: "information",
};

function ResultLineView({ line }: { line: ResultLine }) {
  const { t } = useAppTranslation("containerDetail");
  return (
    <View style={styles.resultLine}>
      <MaterialCommunityIcons name={TONE_ICON[line.tone]} size={20} color={TONE_COLOR[line.tone]} />
      <View style={styles.resultText}>
        <Text style={[styles.resultMain, { color: TONE_COLOR[line.tone] }]}>{t(line.key, line.params)}</Text>
        {line.detail ? <Text style={styles.resultDetail}>{line.detail}</Text> : null}
      </View>
    </View>
  );
}

function RunningView({ message }: { message: string }) {
  return (
    <View style={styles.running}>
      <ActivityIndicator size="large" color={HB_COLORS.action} />
      <Text style={styles.runningText}>{message}</Text>
    </View>
  );
}

// ---------------------------------------------------------------------------
// 创建新商品确认
// ---------------------------------------------------------------------------

export function ContainerDetailCreateProductsSheet({
  visible,
  phase,
  count,
  missingRetailPrice,
  syncToHq,
  onSyncToHqChange,
  onLocate,
  onConfirm,
  onDismiss,
  resultView,
  errorMessage,
}: {
  visible: boolean;
  phase: JobSheetPhase;
  /** 已选的未建档新商品个数 */
  count: number;
  missingRetailPrice: readonly MissingRetailPriceDetail[];
  syncToHq: boolean;
  onSyncToHqChange: (value: boolean) => void;
  /** 定位到该行：由页面关闭本弹窗并滚动表格 */
  onLocate: (hguid: string) => void;
  onConfirm: () => void;
  /** confirm 阶段取消 / result 阶段完成 */
  onDismiss: () => void;
  resultView: CreateNewProductsResultView | null;
  /** 创建任务提交/轮询抛错时的信息（此时创建结果未知） */
  errorMessage: string;
}) {
  const { t } = useAppTranslation("containerDetail");
  const blocked = missingRetailPrice.length > 0;
  const shownMissing = missingRetailPrice.slice(0, MISSING_PRICE_DISPLAY_LIMIT);

  const footer = phase === "confirm" ? (
    <>
      {/* 缺零售价时确认键不可用，并写明原因，避免用户以为按钮坏了 */}
      {blocked ? <Text style={styles.caption}>{t("createProducts.confirmDisabledCaption")}</Text> : null}
      <View style={styles.footerRow}>
        <Button mode="outlined" style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent} onPress={onDismiss}>
          {t("actions.cancel")}
        </Button>
        <Button mode="contained" disabled={blocked} style={[BUSINESS_UI.button, styles.footerButtonWide]} contentStyle={BUSINESS_UI.buttonContent} onPress={onConfirm}>
          {t("createProducts.confirm")}
        </Button>
      </View>
    </>
  ) : phase === "result" ? (
    <Button mode="contained" style={BUSINESS_UI.button} contentStyle={BUSINESS_UI.buttonContent} onPress={onDismiss}>
      {t("actions.done")}
    </Button>
  ) : undefined;

  return (
    <BusinessSheet
      visible={visible}
      title={t("createProducts.title")}
      onDismiss={onDismiss}
      dismissable={phase !== "running"}
      footer={footer}
    >
      {phase === "confirm" ? (
        <>
          <Text style={styles.body}>{t("createProducts.warning", { count })}</Text>
          <Pressable
            accessibilityRole="checkbox"
            accessibilityState={{ checked: syncToHq }}
            onPress={() => onSyncToHqChange(!syncToHq)}
            style={styles.checkboxRow}
          >
            <MaterialCommunityIcons
              name={syncToHq ? "checkbox-marked" : "checkbox-blank-outline"}
              size={24}
              color={syncToHq ? HB_COLORS.action : HB_COLORS.textSecondary}
            />
            <Text style={styles.checkboxLabel}>{t("createProducts.syncToHq")}</Text>
          </Pressable>
          {blocked ? (
            <View style={styles.missingPanel}>
              <Text style={styles.missingTitle}>{t("createProducts.missingTitle", { count: missingRetailPrice.length })}</Text>
              <Text style={styles.missingHint}>{t("createProducts.missingHint")}</Text>
              {shownMissing.map((row) => (
                <View key={row.hguid} style={styles.missingRow}>
                  <View style={styles.missingInfo}>
                    <Text style={styles.missingLabel} numberOfLines={1}>{row.label}</Text>
                    <Text style={styles.missingPrice}>{t("createProducts.retailPrice", { price: formatContainerDetailNumber(row.retailPrice) })}</Text>
                  </View>
                  <Button compact mode="outlined" icon="crosshairs-gps" contentStyle={BUSINESS_UI.buttonContent} onPress={() => onLocate(row.hguid)}>
                    {t("createProducts.locate")}
                  </Button>
                </View>
              ))}
              {missingRetailPrice.length > shownMissing.length ? (
                <Text style={styles.missingHint}>{t("createProducts.missingMore", { count: missingRetailPrice.length - shownMissing.length })}</Text>
              ) : null}
            </View>
          ) : null}
        </>
      ) : null}
      {phase === "running" ? <RunningView message={t(syncToHq ? "createProducts.runningWithHq" : "createProducts.running")} /> : null}
      {phase === "result" ? (
        <>
          {errorMessage ? (
            <ResultLineView line={{ tone: "danger", key: "createProducts.result.creationError", detail: errorMessage }} />
          ) : resultView ? (
            <>
              <ResultLineView line={resultView.creation} />
              {resultView.hq ? <ResultLineView line={resultView.hq} /> : null}
              {resultView.extras.map((line) => <ResultLineView key={line.key} line={line} />)}
            </>
          ) : null}
        </>
      ) : null}
    </BusinessSheet>
  );
}

// ---------------------------------------------------------------------------
// 提交整柜确认
// ---------------------------------------------------------------------------

export function ContainerDetailSubmitSheet({
  visible,
  phase,
  containerNumber,
  rowCount,
  onConfirm,
  onDismiss,
  resultLine,
  errorMessage,
}: {
  visible: boolean;
  phase: JobSheetPhase;
  containerNumber: string;
  /** 整柜明细行数（取不到时为 undefined） */
  rowCount: number | undefined;
  onConfirm: () => void;
  onDismiss: () => void;
  resultLine: ResultLine | null;
  errorMessage: string;
}) {
  const { t } = useAppTranslation("containerDetail");
  const footer = phase === "confirm" ? (
    <View style={styles.footerRow}>
      <Button mode="outlined" style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent} onPress={onDismiss}>
        {t("actions.cancel")}
      </Button>
      <Button mode="contained" style={[BUSINESS_UI.button, styles.footerButtonWide]} contentStyle={BUSINESS_UI.buttonContent} onPress={onConfirm}>
        {t("submitContainer.confirm")}
      </Button>
    </View>
  ) : phase === "result" ? (
    <Button mode="contained" style={BUSINESS_UI.button} contentStyle={BUSINESS_UI.buttonContent} onPress={onDismiss}>
      {t("actions.done")}
    </Button>
  ) : undefined;

  return (
    <BusinessSheet
      visible={visible}
      title={t("submitContainer.title")}
      onDismiss={onDismiss}
      dismissable={phase !== "running"}
      footer={footer}
    >
      {phase === "confirm" ? (
        <>
          <Text style={styles.body}>{t("submitContainer.scope", { container: containerNumber || "--" })}</Text>
          <Text style={styles.caption}>
            {rowCount === undefined ? t("submitContainer.scopeNote") : t("submitContainer.scopeNoteWithRows", { count: rowCount })}
          </Text>
        </>
      ) : null}
      {phase === "running" ? <RunningView message={t("submitContainer.running")} /> : null}
      {phase === "result" ? (
        errorMessage
          ? <ResultLineView line={{ tone: "danger", key: "submitContainer.result.error", detail: errorMessage }} />
          : resultLine ? <ResultLineView line={resultLine} /> : null
      ) : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  body: { color: HB_COLORS.textPrimary, fontSize: 15, lineHeight: 22 },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerButtonWide: { flex: 2 },
  checkboxRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  checkboxLabel: { flex: 1, color: HB_COLORS.textPrimary, fontSize: 14 },
  missingPanel: {
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.danger,
    backgroundColor: HB_COLORS.white,
  },
  missingTitle: { color: HB_COLORS.danger, fontSize: 14, fontWeight: "700" },
  missingHint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  missingRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  missingInfo: { flex: 1, minWidth: 0 },
  missingLabel: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  missingPrice: { color: HB_COLORS.danger, fontSize: 12, fontVariant: ["tabular-nums"] },
  running: { alignItems: "center", gap: HB_SPACING.sm, paddingVertical: HB_SPACING.lg },
  runningText: { color: HB_COLORS.textSecondary, fontSize: 14, textAlign: "center" },
  resultLine: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "flex-start" },
  resultText: { flex: 1, gap: 2 },
  resultMain: { fontSize: 14, lineHeight: 20, fontWeight: "600" },
  resultDetail: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
});
