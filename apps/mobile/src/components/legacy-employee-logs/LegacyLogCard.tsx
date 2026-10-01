import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { clockOf, formatAmountImpact, shortFlagEvidence, storeDisplayName } from "@/modules/legacy-employee-logs/logic";
import type { LegacyLogItem } from "@/modules/legacy-employee-logs/types";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { DangerBadge, OperationTag, ReviewBadge } from "./LegacyLogTags";
import { LEGACY_UI, RISK } from "./ui";

type Translate = (key: string, params?: Record<string, unknown>) => string;

/** 列表卡片：时间、操作、风险徽标、金额一行；标题一行；员工 · 分店 · 设备一行；命中异常时底部一行依据。 */
export const LegacyLogCard = memo(function LegacyLogCard({
  item,
  storeNames,
  t,
  onPress,
}: {
  item: LegacyLogItem;
  storeNames: ReadonlyMap<string, string>;
  t: Translate;
  onPress: (item: LegacyLogItem) => void;
}) {
  const amount = formatAmountImpact(item.amountImpact);
  const flag = item.flags[0];
  const short = flag ? shortFlagEvidence(flag) : null;
  const title = item.title ?? productTitle(item.operationDetail) ?? item.operationDetail ?? item.operation ?? "-";
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${clockOf(item.operationTime)} ${item.operation ?? ""} ${item.employeeName ?? ""}`}
      onPress={() => onPress(item)}
      style={({ pressed }) => [LEGACY_UI.card, styles.card, pressed ? styles.pressed : null]}
    >
      <View style={styles.row}>
        <Text style={[styles.time, LEGACY_UI.mono]}>{clockOf(item.operationTime)}</Text>
        <OperationTag operation={item.operation} tone={item.tone} />
        {item.isDanger ? <DangerBadge label={t("badges.danger")} /> : null}
        <ReviewBadge item={item} reviewedLabel={t("badges.reviewed")} followUpLabel={t("badges.followUp")} />
        {item.pos && item.pos.outcome !== "Succeeded" ? <Text style={styles.outcome}>{t(`outcomes.${item.pos.outcome}`, { defaultValue: item.pos.outcome })}</Text> : null}
        <View style={styles.spacer} />
        {amount ? <Text style={[styles.amount, LEGACY_UI.mono]}>{amount}</Text> : null}
      </View>
      <Text numberOfLines={1} style={styles.title}>{title}</Text>
      <Text numberOfLines={1} style={styles.meta}>
        {[item.employeeName || "-", storeDisplayName(item.storeCode, storeNames), item.deviceCode || "-"].join(" · ")}
      </Text>
      {flag ? (
        <View style={styles.abnormal}>
          <Icon source="pulse" size={13} color={RISK.abnormalIcon} />
          <Text numberOfLines={1} style={styles.abnormalText}>
            <Text style={styles.abnormalStrong}>{t(`rules.${flag.ruleCode}.label`)}</Text>
            {short ? ` · ${t(short.key, short.params)}` : ""}
            {item.flags.length > 1 ? ` +${item.flags.length - 1}` : ""}
          </Text>
        </View>
      ) : null}
    </Pressable>
  );
});

/** 详情文本的商品名（「商品」「从购物车删除商品」键），没有时返回 null。 */
export function productTitle(detail: string | null) {
  if (!detail) return null;
  const match = /(?:从购物车删除商品|商品)[:：]\s*([^，]+)/.exec(detail);
  return match ? match[1].trim() : null;
}

const styles = StyleSheet.create({
  card: { marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  pressed: { opacity: 0.85 },
  row: { flexDirection: "row", alignItems: "center", gap: 6, flexWrap: "wrap" },
  time: { fontSize: 13, color: HB_COLORS.textPrimary },
  spacer: { flex: 1 },
  amount: { fontSize: 14, fontWeight: "700", color: RISK.danger },
  outcome: { fontSize: 11, fontWeight: "600", color: "#A8071A" },
  title: { fontSize: 15, fontWeight: "500", color: HB_COLORS.textPrimary },
  meta: { fontSize: 12, color: HB_COLORS.textSecondary },
  abnormal: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 8,
    paddingVertical: 5,
    borderRadius: 6,
    backgroundColor: RISK.abnormalBg,
  },
  abnormalText: { flex: 1, fontSize: 12, color: RISK.abnormalText },
  abnormalStrong: { fontWeight: "700", color: RISK.abnormalText },
});
