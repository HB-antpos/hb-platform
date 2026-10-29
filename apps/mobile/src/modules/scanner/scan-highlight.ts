import type { ScanHighlightKind } from "@/modules/scanner/scan-bell-tones";
import { shouldAutoPrintDiscountLabel } from "@/modules/product-maintenance/scan-label";

export type { ScanHighlightKind };

export interface ScanHighlightInput {
  /** 本店价折扣率（小数，0.2 = 8 折）；口径与「折扣标签」按钮一致，大于 0 才算有折扣。 */
  discountRate: number | null | undefined;
  /** 当前正在生效的促销条数；未加载、无权限、离线时传 0。 */
  activePromotionCount: number;
  /** 商品下挂的多码数量。 */
  multiCodeCount: number | null | undefined;
}

/**
 * 一次扫描只响一个提示：促销 > 折扣 > 多码。
 * 促销是规则命中、会改变收银价格，最需要提醒；折扣其次；多码只是同一商品的别名条码。
 * 都没有时返回 null，调用方继续播放普通的「找到」提示音。
 */
export function resolveScanHighlight(input: ScanHighlightInput): ScanHighlightKind | null {
  if (input.activePromotionCount > 0) {
    return "promotion";
  }
  if (shouldAutoPrintDiscountLabel(input.discountRate)) {
    return "discount";
  }
  if (typeof input.multiCodeCount === "number" && input.multiCodeCount > 0) {
    return "multi_code";
  }
  return null;
}

export type HapticStrength = "light" | "medium" | "heavy";

export interface HapticPulse {
  /** 相对提示音起点的延迟，与铃音的起音时刻对齐。 */
  delayMs: number;
  strength: HapticStrength;
}

// 震动节奏跟着铃音走：折扣一下，促销由轻到重三下，多码两轻一中。手感上也能区分。
export const SCAN_HIGHLIGHT_HAPTICS: Record<ScanHighlightKind, HapticPulse[]> = {
  discount: [{ delayMs: 0, strength: "medium" }],
  promotion: [
    { delayMs: 0, strength: "light" },
    { delayMs: 170, strength: "medium" },
    { delayMs: 340, strength: "heavy" },
  ],
  multi_code: [
    { delayMs: 0, strength: "light" },
    { delayMs: 140, strength: "light" },
    { delayMs: 280, strength: "medium" },
  ],
};
