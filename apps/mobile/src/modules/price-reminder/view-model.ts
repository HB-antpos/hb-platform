import {
  isStoreOverdueFlagged,
  sortStoreSummaries,
  type PriceTaskOverview,
} from "@/modules/price-updates/overview";
import type { StorePriceUpdateStoreSummary } from "@/modules/price-updates/types";

/** 单店提醒的标题口径：两类都有 / 只剩换标签 / 只剩改价。 */
export type PriceReminderHeadline = "both" | "labelOnly" | "priceOnly";

export interface PriceReminderRowViewModel {
  storeCode: string;
  storeName: string;
  pendingPriceUpdateCount: number;
  pendingLabelOnlyCount: number;
  /** 只有管理分店的超期才标红（与锁报表同口径），其余为 0。 */
  overdueCount: number;
  isCurrent: boolean;
  done: boolean;
}

export interface PriceReminderSingleStoreViewModel {
  storeCode: string;
  storeName: string;
  priceUpdateCount: number;
  labelOnlyCount: number;
  overdueCount: number;
  headline: PriceReminderHeadline;
}

/** 主按钮点击后要去哪里：打开「价格和标签」，可选只看超期，并带上要选中的分店。 */
export interface PriceReminderAction {
  kind: "overdue" | "default";
  storeCode: string | null;
  overdueOnly: boolean;
}

export interface PriceReminderViewModel {
  /** multi = 店长账号多家分店的列表；single = 只有一家店（PDA / 单店账号）的两格计数。 */
  variant: "multi" | "single";
  totalPending: number;
  storeCount: number;
  overdueDays: number;
  rows: PriceReminderRowViewModel[];
  /** 店长账号管理分店有超期：弹窗里的红色锁定框（报表已锁定）。 */
  lock: { overdueCount: number } | null;
  single: PriceReminderSingleStoreViewModel | null;
  primaryAction: PriceReminderAction;
}

function resolveHeadline(row: Pick<StorePriceUpdateStoreSummary, "pendingPriceUpdateCount" | "pendingLabelOnlyCount">): PriceReminderHeadline {
  if (row.pendingPriceUpdateCount > 0 && row.pendingLabelOnlyCount > 0) return "both";
  return row.pendingLabelOnlyCount > 0 ? "labelOnly" : "priceOnly";
}

/**
 * 把概览整理成弹窗要显示的内容。只做取数和排序，不含任何文案：文案在组件里用 i18n 拼，
 * 这里的结构化结果可以直接单测（谁被锁、哪家店排前面、主按钮去哪）。
 * 没有可提醒的内容（概览没就绪或全部完成）时返回 null。
 */
export function buildPriceReminderViewModel(input: {
  overview: PriceTaskOverview;
  currentStoreCode: string | null;
  isDeviceMode: boolean;
}): PriceReminderViewModel | null {
  const { overview } = input;
  if (!overview.ready || overview.totalPending <= 0) {
    return null;
  }

  const sorted = sortStoreSummaries(overview.rows, overview.manageableStoreCodes);
  const rows: PriceReminderRowViewModel[] = sorted.map((row) => ({
    storeCode: row.storeCode,
    storeName: row.storeName,
    pendingPriceUpdateCount: row.pendingPriceUpdateCount,
    pendingLabelOnlyCount: row.pendingLabelOnlyCount,
    overdueCount: isStoreOverdueFlagged(row, overview.manageableStoreCodes) ? row.overdueCount : 0,
    isCurrent: row.storeCode === input.currentStoreCode,
    done: row.pendingCount === 0,
  }));
  const lock = overview.reportsLocked ? { overdueCount: overview.manageableOverdueCount } : null;
  const firstPending = rows.find((row) => !row.done) ?? null;

  const primaryAction: PriceReminderAction = lock
    ? { kind: "overdue", storeCode: overview.firstOverdueStoreCode, overdueOnly: true }
    : { kind: "default", storeCode: firstPending?.storeCode ?? null, overdueOnly: false };

  // PDA 永远是单店；账号只有一家分店时也用单店布局，没必要列出只有一行的列表。
  const useSingle = input.isDeviceMode || sorted.length <= 1;
  if (useSingle) {
    const row = rows[0] ?? null;
    return {
      variant: "single",
      totalPending: overview.totalPending,
      storeCount: rows.length,
      overdueDays: overview.overdueDays,
      rows,
      lock,
      single: row
        ? {
            storeCode: row.storeCode,
            storeName: row.storeName,
            priceUpdateCount: row.pendingPriceUpdateCount,
            labelOnlyCount: row.pendingLabelOnlyCount,
            overdueCount: row.overdueCount,
            headline: resolveHeadline(row),
          }
        : null,
      primaryAction,
    };
  }

  return {
    variant: "multi",
    totalPending: overview.totalPending,
    storeCount: rows.length,
    overdueDays: overview.overdueDays,
    rows,
    lock,
    single: null,
    primaryAction,
  };
}
