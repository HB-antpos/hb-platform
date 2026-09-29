import { HB_COLORS } from "@/shared/theme/tokens";

/** 拣货页的状态色：与 HB_COLORS 同色系，文字色均满足 4.5:1 对比度。 */
export const PICK_COLORS = {
  ...HB_COLORS,
  ink: "#101828",
  inkMuted: "#98A2B3",
  infoBg: "#EFF4FF",
  infoBorder: "#C7D7FE",
  infoText: "#0B3A8C",
  successBg: "#ECFDF3",
  successBorder: "#ABEFC6",
  successText: "#054F31",
  warningBg: "#FFFAEB",
  warningBorder: "#FEDF89",
  warningText: "#93370D",
  warningStrong: "#F79009",
  dangerBg: "#FEF3F2",
  dangerBorder: "#FECDCA",
  dangerText: "#7A271A",
  cellBg: "#F8FAFC",
  neutralChipBg: "#F2F4F7",
  neutralChipText: "#344054",
} as const;

export const MONO_FONT = "monospace";
