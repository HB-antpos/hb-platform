import { HB_COLORS } from "@/shared/theme/tokens";

/**
 * 填报页用到的浅色底与提示色。HB_COLORS 只有主色，这里补齐设计稿里的选中底色、
 * 黄色「已填过」提示与蓝色「未填过」提示，只在本模块使用。
 */
export const SEASONAL_CARD_COLORS = {
  selectedBorder: HB_COLORS.brand,
  selectedBackground: "#EFF6FF",
  selectedText: HB_COLORS.action,
  tagBackground: "#D1E9FF",
  changedRowBackground: "#F5F9FF",
  stepperBackground: "#F9FAFB",
  disabledBackground: HB_COLORS.surfaceMuted,
  disabledText: "#98A2B3",
  mutedText: "#667085",
  warningBackground: "#FFFAEB",
  warningBorder: "#FEDF89",
  warningText: "#93370D",
  infoBackground: "#EFF6FF",
  infoBorder: "#B2DDFF",
  infoText: HB_COLORS.action,
  dangerBackground: "#FEF3F2",
  dangerBorder: "#FECDCA",
  dangerText: HB_COLORS.danger,
} as const;
