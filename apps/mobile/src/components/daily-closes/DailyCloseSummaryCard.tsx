import { StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { formatDailyCloseDifference, formatDailyCloseMoney, type DailyCloseSummaryView } from "@/modules/daily-closes/logic";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { AMOUNT_COLORS, DAILY_UI } from "./ui";

/** 列表顶部摘要卡：当前筛选范围内的净差额（实点 − 应有）与应有 / 实点合计；无金额记录不计入合计。 */
export function DailyCloseSummaryCard({ summary, loading }: { summary: DailyCloseSummaryView; loading: boolean }) {
  const { t } = useAppTranslation("dailyCloses");
  const color = AMOUNT_COLORS[summary.kind];
  return (
    <View style={[DAILY_UI.card, styles.card, loading ? styles.loading : null]} accessibilityRole="summary">
      <View style={styles.row}>
        <View style={styles.main}>
          <Text style={styles.label}>{t("summary.title")}</Text>
          {summary.hasAmounts ? (
            <Text style={[styles.big, DAILY_UI.mono, { color }]} numberOfLines={1} adjustsFontSizeToFit>
              {formatDailyCloseDifference(summary.difference)}
            </Text>
          ) : (
            <Text style={[styles.big, { color: HB_COLORS.textSecondary }]}>—</Text>
          )}
        </View>
        {summary.hasAmounts ? (
          <View style={styles.side}>
            <Text style={[styles.sideText, DAILY_UI.mono]}>
              {t("summary.expected")} <Text style={styles.sideValue}>{formatDailyCloseMoney(summary.expectedCash)}</Text>
            </Text>
            <Text style={[styles.sideText, DAILY_UI.mono]}>
              {t("summary.counted")} <Text style={styles.sideValue}>{formatDailyCloseMoney(summary.countedCash)}</Text>
            </Text>
          </View>
        ) : null}
      </View>
      {!summary.hasAmounts ? <Text style={styles.note}>{t("summary.noAmounts")}</Text> : null}
      {summary.noAmountCount > 0 ? (
        <View style={styles.noteRow}>
          <Icon source="information-outline" size={14} color={HB_COLORS.textSecondary} />
          <Text style={styles.note}>{t("summary.noAmountNote", { count: summary.noAmountCount })}</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { marginHorizontal: HB_SPACING.md, gap: 6 },
  loading: { opacity: 0.6 },
  row: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  main: { flex: 1, minWidth: 0 },
  label: { fontSize: 12, color: HB_COLORS.textSecondary },
  big: { fontSize: 26, lineHeight: 32, fontWeight: "700" },
  side: { alignItems: "flex-end", gap: 2 },
  sideText: { fontSize: 12, color: HB_COLORS.textSecondary },
  sideValue: { fontWeight: "600", color: HB_COLORS.textPrimary },
  noteRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  note: { fontSize: 12, color: HB_COLORS.textSecondary },
});
