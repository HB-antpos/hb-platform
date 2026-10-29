import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { formatPromotionDate } from "@/modules/promotions/promotion-logic";
import type { PromotionListItem } from "@/modules/promotions/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface ProductPromotionConflictState {
  /** 本次保存要用的开关（停用保存不会走到这里）。 */
  isEnabled: boolean;
  conflicts: PromotionListItem[];
  /** 「改到 X 结束」的建议结束时间；整段被覆盖时为 null，不提供该按钮。 */
  suggestedEnd: string | null;
}

interface ConflictContentProps {
  state: ProductPromotionConflictState;
  candidateName: string;
  candidatePriority: number;
  candidateStart: string;
  candidateEnd: string;
  candidateExclusive: boolean;
}

function formatRange(start: string, end: string) {
  return `${formatPromotionDate(start)} – ${formatPromotionDate(end)}`;
}

/**
 * 保存前本地冲突预检的提示内容（设计稿第 3 屏下半部分）。
 * 只是提醒：服务端的排他冲突校验仍是最终防线。
 */
export function ProductPromotionConflictContent({
  state,
  candidateName,
  candidatePriority,
  candidateStart,
  candidateEnd,
  candidateExclusive,
}: ConflictContentProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const first = state.conflicts[0];
  if (!first) return null;
  // 对方是排他 → 「已有排他促销」；对方可叠加而本条排他 → 「本条是排他促销」。
  const messageKey = first.isExclusive || !candidateExclusive ? "promotion.conflict.messageOther" : "promotion.conflict.messageSelf";
  const scope = t(first.scopeType === "MultiStore" ? "promotion.conflict.scopeMulti" : "promotion.conflict.scopeStore");

  return (
    <View style={styles.container}>
      <View style={styles.headline}>
        <Icon source="alert-outline" size={22} color={HB_COLORS.warning} />
        <Text style={styles.message}>
          {t(messageKey, {
            range: formatRange(first.effectiveStart, first.effectiveEnd),
            scope,
            name: first.name || t("promotion.unnamed"),
          })}
        </Text>
      </View>
      <View style={styles.compareBox}>
        <Text style={styles.compareText}>
          {t("promotion.conflict.current", {
            name: candidateName,
            priority: candidatePriority,
            range: formatRange(candidateStart, candidateEnd),
          })}
        </Text>
        {state.conflicts.map((item) => (
          <Text key={item.id} style={styles.compareText}>
            {t("promotion.conflict.other", {
              name: item.name || t("promotion.unnamed"),
              priority: item.priority,
              range: formatRange(item.effectiveStart, item.effectiveEnd),
            })}
          </Text>
        ))}
      </View>
    </View>
  );
}

interface ConflictFooterProps {
  suggestedEnd: string | null;
  saving: boolean;
  onEndBefore: () => void;
  onSaveAnyway: () => void;
}

export function ProductPromotionConflictFooter({
  suggestedEnd,
  saving,
  onEndBefore,
  onSaveAnyway,
}: ConflictFooterProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  return (
    <View style={styles.footer}>
      {suggestedEnd ? (
        <Button
          mode="outlined"
          onPress={onEndBefore}
          disabled={saving}
          style={styles.footerButton}
          contentStyle={styles.buttonContent}
        >
          {t("promotion.conflict.endBefore", { date: formatPromotionDate(suggestedEnd) })}
        </Button>
      ) : null}
      <Button
        mode="contained"
        onPress={onSaveAnyway}
        loading={saving}
        disabled={saving}
        style={styles.footerButton}
        contentStyle={styles.buttonContent}
      >
        {t("promotion.conflict.saveAnyway")}
      </Button>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { gap: HB_SPACING.sm },
  headline: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "flex-start" },
  message: { flex: 1, fontSize: 13, lineHeight: 20, color: HB_COLORS.textSecondary },
  compareBox: {
    gap: 6,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
  },
  compareText: { fontSize: 13, lineHeight: 20, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1, borderRadius: HB_RADIUS.control },
  buttonContent: { minHeight: 44 },
});
