import { StyleSheet } from "react-native";
import type { DailyCloseDifferenceKind } from "@/modules/daily-closes/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 现金差额四态配色：颜色只是辅助，图标 + 文字 + 正负号一起出现（色弱也能分清）。
 * 短款=危险红、长款=警示橙、已平=成功绿、无金额=灰。
 */
export const DIFFERENCE_TONES: Record<DailyCloseDifferenceKind, { text: string; background: string; border: string; icon: string }> = {
  short: { text: HB_COLORS.danger, background: "#FEF3F2", border: "#FDA29B", icon: "triangle-small-down" },
  over: { text: HB_COLORS.warning, background: "#FFFAEB", border: "#FEC84B", icon: "triangle-small-up" },
  even: { text: HB_COLORS.success, background: "#ECFDF3", border: "#ABEFC6", icon: "check-bold" },
  none: { text: HB_COLORS.textSecondary, background: HB_COLORS.surfaceMuted, border: HB_COLORS.outline, icon: "minus" },
};

/** 金额文字色：已平用正文色（不抢眼），无金额用次要文字色。 */
export const AMOUNT_COLORS: Record<DailyCloseDifferenceKind, string> = {
  short: HB_COLORS.danger,
  over: HB_COLORS.warning,
  even: HB_COLORS.textPrimary,
  none: HB_COLORS.textSecondary,
};

/** 补录提示条：蓝色信息色。 */
export const NOTICE = {
  text: "#175CD3",
  background: "#EFF8FF",
  border: "#B2DDFF",
} as const;

/** 0 张的面额行灰显但仍可读（对比度约 4.8:1）。 */
export const DIM_TEXT = "#667085";

export const DAILY_UI = StyleSheet.create({
  mono: { fontVariant: ["tabular-nums"] },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
  },
  sectionLabel: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textSecondary, marginTop: 4 },
  /** 没有数据的区块用虚线占位，不整块隐藏，让用户知道「这里本该有内容」。 */
  placeholder: {
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.sm,
    alignItems: "center",
    backgroundColor: HB_COLORS.surface,
  },
  placeholderText: { fontSize: 12, color: HB_COLORS.textSecondary },
});
