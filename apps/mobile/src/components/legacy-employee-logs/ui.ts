import { StyleSheet } from "react-native";
import type { LegacyOperationTone } from "@/modules/legacy-employee-logs/logic";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 操作标签配色：与 Web 端 antd Tag 的类别色一致（蓝商品、橙价格、红删除、绿结账、洋红退货、紫钱箱身份）。 */
export const OPERATION_TONES: Record<LegacyOperationTone, { text: string; background: string; border: string }> = {
  item: { text: "#0958D9", background: "#E6F4FF", border: "#91CAFF" },
  price: { text: "#AD4E00", background: "#FFF7E6", border: "#FFD591" },
  delete: { text: "#A8071A", background: "#FFF1F0", border: "#FFA39E" },
  payment: { text: "#237804", background: "#F6FFED", border: "#B7EB8F" },
  return: { text: "#9E1068", background: "#FFF0F6", border: "#FFADD2" },
  auth: { text: "#531DAB", background: "#F9F0FF", border: "#D3ADF7" },
  other: { text: "#434343", background: "#FAFAFA", border: "#D9D9D9" },
};

/** 风险配色：危险深红实心，异常琥珀浅底（两者明度不同，不只靠色相区分）。 */
export const RISK = {
  danger: "#B8300A",
  abnormalText: "#5C3A00",
  abnormalBg: "#FFF3D1",
  abnormalBorder: "#E8B13A",
  abnormalIcon: "#C98500",
  followUp: "#5A2BA0",
} as const;

export const LEGACY_UI = StyleSheet.create({
  tag: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 4,
    borderWidth: StyleSheet.hairlineWidth,
    alignSelf: "flex-start",
  },
  tagText: { fontSize: 11, lineHeight: 16 },
  pill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
    paddingHorizontal: 7,
    paddingVertical: 1,
    borderRadius: 999,
    alignSelf: "flex-start",
  },
  pillText: { fontSize: 11, lineHeight: 16, fontWeight: "600" },
  mono: { fontVariant: ["tabular-nums"] },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    gap: 6,
  },
  chip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    minHeight: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  chipOn: { borderColor: HB_COLORS.textPrimary, backgroundColor: HB_COLORS.textPrimary },
  chipText: { fontSize: 13, color: HB_COLORS.textPrimary },
  chipTextOn: { color: HB_COLORS.white },
  chipCount: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  chipCountOn: { color: "#D0D5DD" },
  sectionLabel: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary, marginTop: HB_SPACING.sm, marginBottom: HB_SPACING.xs },
});
