import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { isMySegment, type MineContext, type SegmentSummary } from "../pick-math";
import { shortPickerName } from "../pick-view-model";
import { PICK_COLORS, segmentColor } from "./pick-theme";

/**
 * 还没拣完的段：段号色块、负责人（或待领取）、已处理 / 段内品种数和一个动作。
 * 我的段是“继续拣”，别人的段是“去帮忙”（帮忙不改负责人，扫码照常记在帮忙的人名下）。
 * 完成页的提交拦截框与拣货页“这段拣完了”卡片共用。
 */
export function IncompleteSegmentRows({
  segments,
  mine,
  onHelp,
  onContinue,
}: {
  segments: readonly SegmentSummary[];
  mine: MineContext;
  onHelp: (segment: SegmentSummary) => void;
  onContinue: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <View style={styles.list}>
      {segments.map((segment) => {
        const own = isMySegment(segment, mine);
        const done = segment.completeLineCount + segment.stockoutLineCount;
        return (
          <View key={segment.segmentNo} style={styles.row}>
            <View style={[styles.badge, { backgroundColor: segmentColor(segment.segmentNo) }]}>
              <Text style={styles.badgeText}>{segment.segmentNo}</Text>
            </View>
            <View style={styles.text}>
              <Text numberOfLines={1} style={[styles.name, !segment.assigneeName ? styles.claimable : null]}>
                {segment.assigneeName ? shortPickerName(segment.assigneeName) : t("finish.segmentClaimable")}
                {own ? ` · ${t("picking.scopeMine")}` : ""}
              </Text>
              <Text style={styles.meta}>{t("picking.segmentSettled", { done, total: segment.lineCount })}</Text>
            </View>
            <Pressable
              accessibilityRole="button"
              onPress={() => (own ? onContinue() : onHelp(segment))}
              style={[styles.action, own ? styles.actionOwn : null]}
            >
              <Text style={[styles.actionText, own ? styles.actionOwnText : null]}>
                {own ? t("picking.segmentContinue") : t("picking.segmentHelp")}
              </Text>
            </Pressable>
          </View>
        );
      })}
    </View>
  );
}

/** 拣货页：当前范围（我的 / 帮的那段）拣完了，还有别的段没完时提示去帮忙。 */
export function HelpOthersCard({
  segments,
  mine,
  onHelp,
}: {
  segments: readonly SegmentSummary[];
  mine: MineContext;
  onHelp: (segment: SegmentSummary) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <View accessibilityLiveRegion="polite" style={styles.card}>
      <Text style={styles.cardTitle}>{t("picking.helpDoneTitle")}</Text>
      <Text style={styles.cardHint}>{t("picking.helpDoneHint", { count: segments.length })}</Text>
      <IncompleteSegmentRows segments={segments} mine={mine} onHelp={onHelp} onContinue={() => undefined} />
    </View>
  );
}

const styles = StyleSheet.create({
  list: { gap: 8 },
  row: { flexDirection: "row", alignItems: "center", gap: 10, minHeight: 48 },
  badge: { width: 26, height: 26, borderRadius: 13, alignItems: "center", justifyContent: "center" },
  badgeText: { color: PICK_COLORS.white, fontSize: 13, fontWeight: "700" },
  text: { flex: 1, minWidth: 0 },
  name: { fontSize: 15, fontWeight: "600", color: PICK_COLORS.ink },
  claimable: { color: PICK_COLORS.warningText },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  action: {
    minHeight: 40,
    minWidth: 76,
    paddingHorizontal: 14,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: PICK_COLORS.action,
  },
  actionText: { color: PICK_COLORS.white, fontSize: 14, fontWeight: "700" },
  actionOwn: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted },
  actionOwnText: { color: PICK_COLORS.ink },
  card: {
    gap: 8,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: PICK_COLORS.infoBorder,
    backgroundColor: PICK_COLORS.infoBg,
  },
  cardTitle: { fontSize: 16, fontWeight: "700", color: PICK_COLORS.ink },
  cardHint: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.infoText },
});
