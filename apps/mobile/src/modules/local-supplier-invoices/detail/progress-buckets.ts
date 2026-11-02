import {
  DetailAction,
  EXECUTED_DETAIL_ACTION,
  getProductStatusFilter,
  resolveDetailAction,
  type DetailStatusSource,
  type RowActions,
} from "./detail-status";

/**
 * 明细处理进度分段（与 Web InvoiceEdit/progressBuckets.ts 对齐）。五段互斥、合计等于明细行数：
 * - executed：批量执行成功后后端写回的 99「已执行」
 * - unchecked：还没做商品检测（existingProductCount 为空）；未检测行即使手工设了操作类型也先归这里，
 *   避免「执行全部待执行」把没检测过的行也送去新建商品
 * - pending：已设置会被批量执行处理的操作（新建商品 / 更新进货价 / 更新货号 / 添加多码）
 * - waiting：等待操作，需要人工确认
 * - none：无需操作（如进货价为空时后端默认的「无」）
 */
export type DetailProgressBucket = "executed" | "pending" | "waiting" | "unchecked" | "none";
export type DetailProgressBucketFilter = "all" | DetailProgressBucket;

export const DETAIL_PROGRESS_BUCKETS: readonly DetailProgressBucket[] = [
  "executed",
  "pending",
  "waiting",
  "unchecked",
  "none",
];

/** 分段颜色（与 Web index.tsx PROGRESS_BUCKET_COLORS 一致）：已执行绿、待执行蓝、等待橙、未检测紫、无需操作灰。 */
export const DETAIL_PROGRESS_BUCKET_COLORS: Record<DetailProgressBucket, string> = {
  executed: "#389e0d",
  unchecked: "#8c6bd8",
  pending: "#1677ff",
  waiting: "#fa8c16",
  none: "#c9d0db",
};

export const PENDING_DETAIL_ACTIONS = [
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
] as const;

export type PendingDetailAction = (typeof PENDING_DETAIL_ACTIONS)[number];

export interface DetailProgressStats {
  total: number;
  executed: number;
  pending: number;
  waiting: number;
  unchecked: number;
  none: number;
  /** 待执行按操作类型拆分，用于「新建 5 · 进货价 11」这类提示。 */
  pendingByAction: Record<PendingDetailAction, number>;
}

export function getDetailProgressBucket(
  detail: DetailStatusSource,
  rowActions: RowActions = {}
): DetailProgressBucket {
  const action = resolveDetailAction(detail, rowActions);
  if (action === EXECUTED_DETAIL_ACTION) return "executed";
  // 未检测优先于已设置的操作类型。
  if (getProductStatusFilter(detail) === "notDetected") return "unchecked";
  if ((PENDING_DETAIL_ACTIONS as readonly number[]).includes(action)) return "pending";
  if (action === DetailAction.WaitForOperation) return "waiting";
  return "none";
}

export function getDetailProgressStats(
  details: readonly DetailStatusSource[],
  rowActions: RowActions = {}
): DetailProgressStats {
  const stats: DetailProgressStats = {
    total: details.length,
    executed: 0,
    pending: 0,
    waiting: 0,
    unchecked: 0,
    none: 0,
    pendingByAction: {
      [DetailAction.CreateProduct]: 0,
      [DetailAction.UpdatePurchasePrice]: 0,
      [DetailAction.UpdateItemNumber]: 0,
      [DetailAction.AddMultiCode]: 0,
    },
  };

  for (const detail of details) {
    const bucket = getDetailProgressBucket(detail, rowActions);
    stats[bucket] += 1;
    if (bucket === "pending") {
      stats.pendingByAction[resolveDetailAction(detail, rowActions) as PendingDetailAction] += 1;
    }
  }

  return stats;
}

export function filterDetailsByProgressBucket<T extends DetailStatusSource>(
  details: readonly T[],
  bucket: DetailProgressBucketFilter,
  rowActions: RowActions = {}
): T[] {
  if (bucket === "all") return [...details];
  return details.filter((detail) => getDetailProgressBucket(detail, rowActions) === bucket);
}

/** 「待执行」的目标行 detailGuid：按全部明细计算，不受当前搜索和筛选影响。 */
export function getPendingExecutionDetailGuids(
  details: readonly DetailStatusSource[],
  rowActions: RowActions = {}
): string[] {
  return details
    .filter((detail) => detail.detailGuid && getDetailProgressBucket(detail, rowActions) === "pending")
    .map((detail) => detail.detailGuid);
}

/** 已执行占比（整数百分比，四舍五入），明细为空时为 0。 */
export function getExecutedPercent(stats: Pick<DetailProgressStats, "total" | "executed">): number {
  if (!stats.total) return 0;
  return Math.round((stats.executed / stats.total) * 100);
}
