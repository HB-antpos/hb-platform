import type { PriceNotificationPreview } from "./types";

export const PRICE_NOTIFICATION_HEADER = "x-price-notification";

export interface PriceNotificationSummary {
  productCount: number;
  needsPriceUpdateStores: number;
  labelOnlyStores: number;
  cancelledStores: number;
  skippedSpecialStores: number;
  hasAny: boolean;
}

/** 文案统一走 priceUpdates 命名空间的 notification.* 键，由调用方注入翻译函数以保持纯函数可测。 */
export type PriceNotificationTranslate = (key: string, params?: Record<string, unknown>) => string;

function toCount(value: unknown): number {
  const numeric = typeof value === "string" && value.trim() ? Number(value) : value;
  return typeof numeric === "number" && Number.isFinite(numeric) && numeric > 0 ? Math.trunc(numeric) : 0;
}

function readHeaderValue(headers: unknown): unknown {
  if (!headers || typeof headers !== "object") {
    return undefined;
  }
  // AxiosHeaders 的 get 大小写不敏感；普通对象（测试替身、fetch 适配）按小写名兜底遍历。
  const getter = (headers as { get?: unknown }).get;
  if (typeof getter === "function") {
    const value = getter.call(headers, PRICE_NOTIFICATION_HEADER);
    if (value != null) {
      return value;
    }
  }
  for (const [name, value] of Object.entries(headers as Record<string, unknown>)) {
    if (name.toLowerCase() === PRICE_NOTIFICATION_HEADER) {
      return value;
    }
  }
  return undefined;
}

/**
 * 解析 X-Price-Notification 响应头。
 * 头不存在或内容损坏都返回 null，表示「本次请求不涉及价格通知」，调用方不得提示。
 */
export function parsePriceNotificationHeader(headers: unknown): PriceNotificationSummary | null {
  const raw = readHeaderValue(headers);
  const text = Array.isArray(raw) ? raw[raw.length - 1] : raw;
  if (typeof text !== "string" || !text.trim()) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(text);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return null;
    }
    const data = parsed as Record<string, unknown>;
    return {
      productCount: toCount(data.productCount ?? data.ProductCount),
      needsPriceUpdateStores: toCount(data.needsPriceUpdateStores ?? data.NeedsPriceUpdateStores),
      labelOnlyStores: toCount(data.labelOnlyStores ?? data.LabelOnlyStores),
      cancelledStores: toCount(data.cancelledStores ?? data.CancelledStores),
      skippedSpecialStores: toCount(data.skippedSpecialStores ?? data.SkippedSpecialStores),
      hasAny: Boolean(data.hasAny ?? data.HasAny),
    };
  } catch {
    return null;
  }
}

/**
 * 一次保存连发多个请求时只取最后一个带头的响应，不能相加（后端每个响应已是累计口径，相加会重复计数）。
 */
export function pickLastPriceNotification(
  summaries: readonly (PriceNotificationSummary | null | undefined)[]
): PriceNotificationSummary | null {
  for (let index = summaries.length - 1; index >= 0; index -= 1) {
    const summary = summaries[index];
    if (summary) {
      return summary;
    }
  }
  return null;
}

/**
 * 仓库侧保存成功后的提示。返回 null = 响应未带通知头，调用方沿用原有「已保存」。
 */
export function buildPriceNotificationSaveMessage(
  summary: PriceNotificationSummary | null | undefined,
  t: PriceNotificationTranslate
): string | null {
  if (!summary) {
    return null;
  }

  const segments: string[] = [];
  if (summary.needsPriceUpdateStores > 0) {
    segments.push(t("notification.needsPriceUpdate", { count: summary.needsPriceUpdateStores }));
  }
  if (summary.labelOnlyStores > 0) {
    segments.push(t("notification.labelOnly", { count: summary.labelOnlyStores }));
  }

  if (segments.length > 0) {
    if (summary.skippedSpecialStores > 0) {
      segments.push(t("notification.skippedSpecial", { count: summary.skippedSpecialStores }));
    }
    const sent = t("notification.savedAndSent", { detail: segments.join(" · ") });
    return summary.cancelledStores > 0
      ? `${sent}${t("notification.cancelledSuffix", { count: summary.cancelledStores })}`
      : sent;
  }

  if (summary.cancelledStores > 0) {
    return t("notification.reverted", { count: summary.cancelledStores });
  }

  return summary.skippedSpecialStores > 0
    ? t("notification.savedOnlySkipped", { count: summary.skippedSpecialStores })
    : t("notification.savedNoNotification");
}

/** 商品维护页「同步其它分店」成功提示；无通知头时只报告同步家数。 */
export function buildSyncToOtherStoresMessage(
  updatedStoreCount: number,
  summary: PriceNotificationSummary | null | undefined,
  t: PriceNotificationTranslate,
  /** 仓库商品同步时后端一并更新了仓库价：提示末尾补一句，让店长知道仓库价也跟着变了。 */
  warehouseUpdated = false
): string {
  const base = summary
    ? t("notification.syncedStoresWithLabels", {
        count: updatedStoreCount,
        labelCount: summary.labelOnlyStores,
      })
    : t("notification.syncedStores", { count: updatedStoreCount });
  return warehouseUpdated ? `${base}${t("notification.warehouseUpdatedSuffix")}` : base;
}

/**
 * 保存前预告。syncStoreRetailPrices=true 表示零售价会自动下发到分店（分店只需换标签），
 * false 表示仅改仓库，由价格不同的分店在「价格更新」里确认。受影响分店为 0 时不显示。
 */
export function buildPriceNotificationPreviewMessage(
  preview: PriceNotificationPreview | null | undefined,
  syncStoreRetailPrices: boolean,
  t: PriceNotificationTranslate
): string | null {
  if (!preview || preview.affectedStores <= 0) {
    return null;
  }
  const base = t(syncStoreRetailPrices ? "notification.previewAutoSync" : "notification.previewNeedsUpdate", {
    count: preview.affectedStores,
  });
  return preview.skippedSpecialStores > 0
    ? `${base}${t("notification.previewSkippedSuffix", { count: preview.skippedSpecialStores })}`
    : base;
}
