import type { ScanFeedbackState } from "@/modules/scanner/types";
import { formatSupplyExpected } from "@/modules/supply-notice/format-expected";
import type { StoreSupplyStatus } from "@/modules/supply-notice/types";

export type OrderNoticeTone = "success" | "info" | "warning" | "error" | "paused";

/** 底部操作栏里闪现的反馈；位置固定，不会推动列表。 */
export interface OrderNotice {
  tone: OrderNoticeTone;
  title: string;
  detail?: string;
}

export const ORDER_NOTICE_DURATION_MS = 2500;

type Translate = (key: string, options?: Record<string, unknown>) => string;

/**
 * 暂停供货的补充说明：后续计划 + 预计恢复时间（不再供应时只给计划）。
 * 调用方的 t 需已加载 supplyNotice 命名空间。
 */
export function describeSupplyStatus(status: StoreSupplyStatus, t: Translate) {
  const plan = t(`supplyNotice:storeTitle.${status.supplyPlan}`);
  if (status.supplyPlan === "Discontinued") {
    return plan;
  }

  const expected = formatSupplyExpected(status, (key, params) => t(`supplyNotice:${key}`, params));
  return `${plan} · ${t("supplyNotice:expectedLabel")} ${expected}`;
}

/**
 * 把扫码 Hook 的反馈映射成操作栏提示。
 * ready / scanning / found / multiple 由页面其它区域承接，这里不闪现。
 */
export function mapScanFeedbackToNotice(feedback: ScanFeedbackState, t: Translate): OrderNotice | null {
  switch (feedback.status) {
    case "ready":
    case "scanning":
    case "found":
    case "multiple":
      return null;
    case "added":
      return {
        tone: "success",
        title: feedback.productName
          ? t("common:orderNotice.addedNamed", { name: feedback.productName })
          : feedback.message,
        detail: feedback.addedQuantity
          ? t("common:orderNotice.addedQuantity", { quantity: feedback.addedQuantity })
          : undefined,
      };
    case "supply_paused":
      return {
        tone: "paused",
        title: t("supplyNotice:scanPausedNamed", {
          name: feedback.productName || feedback.itemNumber || feedback.barcode || "",
        }),
        detail: feedback.supplyStatus
          ? describeSupplyStatus(feedback.supplyStatus, t)
          : feedback.itemNumber || feedback.barcode,
      };
    case "not_found":
      return { tone: "warning", title: feedback.message, detail: feedback.barcode };
    case "blocked":
      return { tone: "warning", title: feedback.message };
    case "price_update_required":
      return { tone: "info", title: feedback.message, detail: feedback.productName };
    case "error":
      return { tone: "error", title: feedback.message, detail: feedback.barcode };
    default:
      return null;
  }
}
