import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import {
  clientKindLabelKey,
  formatDailyCloseDifference,
  formatDailyCloseMoney,
  formatSavedLabel,
  isBackfilledRecord,
  saveSequenceMark,
} from "@/modules/daily-closes/logic";
import type { DailyCloseListItem } from "@/modules/daily-closes/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { DifferenceTag, MarkChip } from "./DailyCloseTags";
import { AMOUNT_COLORS, DAILY_UI } from "./ui";

/**
 * 列表卡片：店名 + 终端 + 「第 N 次」+ 差额金额一行；收银员 · 来源 · 保存时间 + 状态标签一行；
 * 「应有 → 实点」一行；补录 / 推算标记单独一行（有才显示）。差额金额与状态标签放在同一视线区域，是第一信息。
 */
export const DailyCloseCard = memo(function DailyCloseCard({ item, onPress }: { item: DailyCloseListItem; onPress: (item: DailyCloseListItem) => void }) {
  const { t } = useAppTranslation("dailyCloses");
  const kind = item.differenceKind;
  const nth = saveSequenceMark(item);
  const backfill = isBackfilledRecord(item);
  const sourceKey = clientKindLabelKey(item.clientKind);
  const source = sourceKey ? t(`source.${sourceKey}`) : item.clientKind || "-";
  const hasAmounts = item.expectedCashAmount !== null && item.countedCashAmount !== null;
  const store = item.storeName || item.storeCode || "-";
  const statusLabel = t(`status.${kind}`);
  // 无金额：右上角只放一个「—」，状态标签已经写明「无金额」，不重复
  const amount = formatDailyCloseDifference(item.cashDifference);
  const meta = [item.cashierName || item.cashierId || "-", source, t("card.saved", { time: formatSavedLabel(item.savedAtUtc, item.storeTimeZoneId, item.businessDate) })].join(" · ");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${store} ${item.deviceCode} ${statusLabel} ${kind === "none" ? "" : amount}`.trim()}
      onPress={() => onPress(item)}
      style={({ pressed }) => [DAILY_UI.card, styles.card, pressed ? styles.pressed : null]}
    >
      <View style={styles.row}>
        <View style={styles.titleBox}>
          <Text numberOfLines={1} style={styles.store}>
            {store}
            <Text style={styles.device}>{`  ${item.deviceCode}`}</Text>
          </Text>
          {nth !== null ? <MarkChip label={t("mark.nth", { n: nth })} /> : null}
        </View>
        <Text style={[styles.amount, DAILY_UI.mono, { color: AMOUNT_COLORS[kind] }]}>{amount}</Text>
      </View>
      <View style={styles.row}>
        <Text numberOfLines={1} style={styles.meta}>
          {meta}
        </Text>
        <DifferenceTag kind={kind} label={statusLabel} />
      </View>
      {hasAmounts ? (
        <Text style={[styles.compare, DAILY_UI.mono]}>
          {t("card.compare", { expected: formatDailyCloseMoney(item.expectedCashAmount), counted: formatDailyCloseMoney(item.countedCashAmount) })}
        </Text>
      ) : (
        <Text style={styles.compare}>{t("card.noAmountHint")}</Text>
      )}
      {backfill || item.businessDateInferred ? (
        <View style={styles.marks}>
          {backfill ? <MarkChip label={t("mark.backfill")} emphasis /> : null}
          {item.businessDateInferred ? <MarkChip label={t("mark.inferred")} emphasis /> : null}
        </View>
      ) : null}
    </Pressable>
  );
});

const styles = StyleSheet.create({
  card: { marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs, gap: 5 },
  pressed: { opacity: 0.85 },
  row: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  titleBox: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "center", gap: 6 },
  store: { flexShrink: 1, fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  device: { fontSize: 13, fontWeight: "400", color: HB_COLORS.textSecondary },
  amount: { fontSize: 17, fontWeight: "700" },
  meta: { flex: 1, minWidth: 0, fontSize: 12, color: HB_COLORS.textSecondary },
  compare: { fontSize: 12, color: HB_COLORS.textSecondary },
  marks: { flexDirection: "row", gap: 6 },
});
