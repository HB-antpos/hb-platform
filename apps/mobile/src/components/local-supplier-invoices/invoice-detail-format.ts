import { HB_COLORS } from "@/shared/theme/tokens";
import type { DetailProgressBucket } from "@/modules/local-supplier-invoices/detail";

export type TagTone = "blue" | "green" | "orange" | "red" | "gray" | "purple" | "cyan";

/** 标签配色与 Web 明细页一致：底色 + 深色字（对比度满足 AA）。 */
export const TAG_COLORS: Record<TagTone, { bg: string; fg: string }> = {
  blue: { bg: "#EAF2FF", fg: "#073B83" },
  green: { bg: "#E7F6EC", fg: "#054F31" },
  orange: { bg: "#FEF0C7", fg: "#93370D" },
  red: { bg: "#FEE4E2", fg: "#7A271A" },
  gray: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary },
  purple: { bg: "#EEE9FE", fg: "#4A1FB8" },
  cyan: { bg: "#D9F3F5", fg: "#0B5560" },
};

export function formatMoney(value?: number | null) {
  return value == null || Number.isNaN(value) ? "--" : `$${value.toFixed(2)}`;
}

export function formatQuantity(value?: number | null) {
  return value == null || Number.isNaN(value) ? "--" : value.toLocaleString("en-AU");
}

/** 只读视图「总部处理」列：由进度分桶映射（与 Web 一致）。 */
export const HQ_BUCKET_TONE: Record<DetailProgressBucket, TagTone> = {
  executed: "green",
  pending: "blue",
  waiting: "orange",
  unchecked: "gray",
  none: "gray",
};

/** 操作类型标签配色（与 Web DETAIL_ACTION_CONFIG 一致）。 */
export const ACTION_TONE: Record<number, TagTone> = {
  0: "gray",
  1: "blue",
  2: "green",
  3: "orange",
  4: "purple",
  5: "cyan",
  99: "gray",
};

export function formatOrderDate(value?: string | null) {
  if (!value) return "--";
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(value);
  return match ? match[1] : value;
}
