import { HB_COLORS } from "@/shared/theme/tokens";

/** 订货页与购物车共用的视觉常量；只服务这两个业务页，不改全局主题。 */
export const ORDER_COLORS = {
  inCartRow: "#F5F9FF",
  mutedRow: "#F9FAFB",
  stepperBorder: "#B2CCFF",
  tonalBackground: "#EAF2FF",
  tonalText: "#073B83",
  subtleText: "#667085",
  placeholderIcon: "#98A2B3",
  danger: HB_COLORS.danger,
  dangerBackground: "#FEE4E2",
  paused: "#344054",
  barBackground: "#101828",
  barButton: "#1D2939",
  barText: "#FFFFFF",
  barSubtext: "#D0D5DD",
} as const;

// 等级色沿用原 A 紫 / B 蓝 / C 橙 / D 红，改为浅底深字，文字对比度 ≥4.5:1。
const GRADE_COLORS: Record<string, { background: string; text: string }> = {
  A: { background: "#F4EBFF", text: "#5925DC" },
  B: { background: "#EAF2FF", text: "#0958D9" },
  C: { background: "#FFF4E5", text: "#B54708" },
  D: { background: "#FEE4E2", text: "#B42318" },
};

const FALLBACK_GRADE_COLORS = { background: "#F2F4F7", text: "#475467" };

export function normalizeGrade(value: string | null | undefined) {
  return value?.trim().toUpperCase() || "";
}

export function resolveGradeColors(grade: string | null | undefined) {
  return GRADE_COLORS[normalizeGrade(grade)] ?? FALLBACK_GRADE_COLORS;
}

export function formatOrderMoney(value: number | null | undefined) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "--";
  }

  return `$${value.toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ",")}`;
}

/** 起订量小于 1 时按 1 处理，与加减步长保持一致。 */
export function resolveOrderStep(minOrderQuantity: number | null | undefined) {
  return typeof minOrderQuantity === "number" && minOrderQuantity > 0 ? minOrderQuantity : 1;
}

export function resolveTotalPages(total: number, pageSize: number) {
  if (!Number.isFinite(total) || total <= 0 || pageSize <= 0) {
    return 1;
  }

  return Math.max(1, Math.ceil(total / pageSize));
}
