import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { resolveSalesOrderStatusKey } from "@/modules/sales-orders/logic";
import type { SalesOrderRefundTone } from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

const TONES: Record<string, { background: string; color: string }> = {
  pending: { background: "#FAEEDA", color: "#854F0B" },
  paid: { background: "#E1F5EE", color: HB_COLORS.success },
  cancelled: { background: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
  refunded: { background: "#FCEBEB", color: HB_COLORS.danger },
  installment: { background: "#E6F1FB", color: HB_COLORS.action },
  unknown: { background: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
};

/**
 * 含退款商品的订单按整单金额着色：金额不为负是部分退款（橙），为负是整单退款（红）。
 * 标签、卡片金额、详情金额、筛选面板的档位图例共用这一组颜色，保证「已退款」过滤与列表颜色对得上。
 */
export const REFUND_TONES: Record<SalesOrderRefundTone, { background: string; color: string }> = {
  partial: { background: "#FDEBD8", color: HB_COLORS.warning },
  full: { background: "#FCEBEB", color: HB_COLORS.danger },
};

export function SalesOrderStatusTag({
  status,
  refundTone,
}: {
  status: number | null;
  refundTone?: SalesOrderRefundTone | null;
}) {
  const { t } = useAppTranslation("salesOrders");
  const key = resolveSalesOrderStatusKey(status);
  // 「已退款」状态的标签随整单金额变色；其它状态保持原色，退款提示另用 SalesOrderRefundTag。
  const tone = key === "refunded" && refundTone ? REFUND_TONES[refundTone] : (TONES[key] ?? TONES.unknown);
  return (
    <View style={[styles.tag, { backgroundColor: tone.background }]}>
      <Text style={[styles.text, { color: tone.color }]}>{t(`status.${key}`)}</Text>
    </View>
  );
}

/** 非「已退款」状态但订单里含退款商品时的附加标签：部分退款（橙）/ 整单退款（红）。 */
export function SalesOrderRefundTag({ tone }: { tone: SalesOrderRefundTone }) {
  const { t } = useAppTranslation("salesOrders");
  const colors = REFUND_TONES[tone];
  return (
    <View style={[styles.tag, { backgroundColor: colors.background }]}>
      <Text style={[styles.text, { color: colors.color }]}>{t(`refund.${tone}`)}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  tag: { paddingHorizontal: 8, paddingVertical: 2, borderRadius: 6 },
  text: { fontSize: 11, fontWeight: "700" },
});
