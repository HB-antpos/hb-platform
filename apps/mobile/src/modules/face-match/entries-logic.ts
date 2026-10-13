// 名单条目列表与复核任务列表的纯逻辑：状态筛选、查询参数、分页合并、行内操作可用性。
import {
  CATEGORY_ORGANISED_THEFT,
  CATEGORY_OTHER,
  CATEGORY_VIOLENCE,
  ENTRY_STATUS_ACTIVE,
  ENTRY_STATUS_EXPIRED,
  ENTRY_STATUS_PENDING,
  ENTRY_STATUS_WITHDRAWN,
  REVIEW_STATUS_CONFIRMED,
  REVIEW_STATUS_DISMISSED,
  REVIEW_STATUS_PENDING,
} from "./constants";
import type { FaceMatchPage, FaceMatchPermissionFlags, FaceUnwelcomeEntry, FaceUnwelcomeListQuery } from "./types";

// ───────────── 事件类别 ─────────────

/** 事件类别选项：没有「一般偷窃」这一项；文案键在 faceMatch 命名空间。 */
export const INCIDENT_CATEGORY_OPTIONS = [
  { value: CATEGORY_VIOLENCE, labelKey: "unwelcome.category.violence" },
  { value: CATEGORY_ORGANISED_THEFT, labelKey: "unwelcome.category.organisedTheft" },
  { value: CATEGORY_OTHER, labelKey: "unwelcome.category.other" },
] as const;

export function incidentCategoryLabelKey(category: number): string | null {
  return INCIDENT_CATEGORY_OPTIONS.find((option) => option.value === category)?.labelKey ?? null;
}

// ───────────── 条目状态 ─────────────

export type EntryStatusFilterKey = "all" | "pending" | "active" | "expired" | "withdrawn";

export const ENTRY_STATUS_FILTERS: readonly { key: EntryStatusFilterKey; status?: number; labelKey: string }[] = [
  { key: "all", labelKey: "unwelcome.filters.all" },
  { key: "pending", status: ENTRY_STATUS_PENDING, labelKey: "unwelcome.status.pending" },
  { key: "active", status: ENTRY_STATUS_ACTIVE, labelKey: "unwelcome.status.active" },
  { key: "expired", status: ENTRY_STATUS_EXPIRED, labelKey: "unwelcome.status.expired" },
  { key: "withdrawn", status: ENTRY_STATUS_WITHDRAWN, labelKey: "unwelcome.status.withdrawn" },
];

export function entryStatusFromFilter(key: EntryStatusFilterKey): number | undefined {
  return ENTRY_STATUS_FILTERS.find((filter) => filter.key === key)?.status;
}

export const ENTRY_STATUS_LABEL_KEYS: Record<number, string> = {
  [ENTRY_STATUS_PENDING]: "unwelcome.status.pending",
  [ENTRY_STATUS_ACTIVE]: "unwelcome.status.active",
  [ENTRY_STATUS_EXPIRED]: "unwelcome.status.expired",
  [ENTRY_STATUS_WITHDRAWN]: "unwelcome.status.withdrawn",
};

export type StatusTone = "warning" | "success" | "danger" | "neutral";

export function entryStatusTone(status: number): StatusTone {
  if (status === ENTRY_STATUS_PENDING) return "warning";
  if (status === ENTRY_STATUS_ACTIVE) return "success";
  return "neutral";
}

export function canWithdrawEntry(entry: Pick<FaceUnwelcomeEntry, "status">): boolean {
  return entry.status === ENTRY_STATUS_PENDING || entry.status === ENTRY_STATUS_ACTIVE;
}

/** 批准进度文案用的数字，例如 1 / 2（以服务端 approvalsDone / approvalsNeeded 为准）。 */
export function formatApprovalProgress(entry: Pick<FaceUnwelcomeEntry, "approvalsDone" | "approvalsNeeded">): string {
  return `${entry.approvalsDone} / ${entry.approvalsNeeded}`;
}

export interface EntryActionAccess {
  approve: boolean;
  dispute: boolean;
  withdraw: boolean;
}

/**
 * 条目行内操作的可用性。
 * 批准：只认服务端的 canApprove（含提交人 ≠ 批准人、管理员可自批、Other 需两位等规则）再加 Approve 权限标志；
 * 申诉备注 / 撤回：需要 Manage 权限标志，撤回还要求条目处于待批准或生效中。功能未开启时一律只读。
 */
export function resolveEntryActions(
  entry: Pick<FaceUnwelcomeEntry, "status" | "canApprove">,
  permissions: Pick<FaceMatchPermissionFlags, "unwelcomeManage" | "unwelcomeApprove">,
  writeBlocked: boolean,
): EntryActionAccess {
  if (writeBlocked) return { approve: false, dispute: false, withdraw: false };
  return {
    approve: entry.canApprove && permissions.unwelcomeApprove,
    dispute: permissions.unwelcomeManage,
    withdraw: permissions.unwelcomeManage && canWithdrawEntry(entry),
  };
}

/** 是否需要多位批准人（用于批准确认框的提示）。 */
export function needsMultipleApprovals(entry: Pick<FaceUnwelcomeEntry, "approvalsNeeded">): boolean {
  return entry.approvalsNeeded > 1;
}

/** 生效门店标签：最多展示 max 个，其余折叠成「+N」。 */
export function summarizeStoreScope(scope: readonly string[], max = 3): { visible: string[]; extra: number } {
  return { visible: scope.slice(0, max), extra: Math.max(0, scope.length - max) };
}

// ───────────── 复核任务状态 ─────────────

export type ReviewStatusFilterKey = "pending" | "dismissed" | "confirmed";

export const REVIEW_STATUS_FILTERS: readonly { key: ReviewStatusFilterKey; status: number; labelKey: string }[] = [
  { key: "pending", status: REVIEW_STATUS_PENDING, labelKey: "review.status.pending" },
  { key: "dismissed", status: REVIEW_STATUS_DISMISSED, labelKey: "review.status.dismissed" },
  { key: "confirmed", status: REVIEW_STATUS_CONFIRMED, labelKey: "review.status.confirmed" },
];

export const REVIEW_STATUS_LABEL_KEYS: Record<number, string> = {
  [REVIEW_STATUS_PENDING]: "review.status.pending",
  [REVIEW_STATUS_DISMISSED]: "review.status.dismissed",
  [REVIEW_STATUS_CONFIRMED]: "review.status.confirmed",
};

export function reviewStatusFromFilter(key: ReviewStatusFilterKey): number {
  return REVIEW_STATUS_FILTERS.find((filter) => filter.key === key)?.status ?? REVIEW_STATUS_PENDING;
}

export function reviewStatusTone(status: number): StatusTone {
  if (status === REVIEW_STATUS_PENDING) return "warning";
  if (status === REVIEW_STATUS_CONFIRMED) return "danger";
  return "neutral";
}

// ───────────── 查询参数与分页 ─────────────

/** 与 Web 的 listParams 一致：status 以字符串发送，空值不带。 */
export function buildListParams(query: FaceUnwelcomeListQuery): {
  status: string | undefined;
  storeCode: string | undefined;
  page: number;
  pageSize: number;
} {
  return {
    status: query.status === undefined ? undefined : String(query.status),
    storeCode: query.storeCode || undefined,
    page: query.page,
    pageSize: query.pageSize,
  };
}

/** 无限列表的下一页页码：服务端 hasMore 为 false 时没有下一页。 */
export function nextPageParam(lastPage: Pick<FaceMatchPage<unknown>, "page" | "hasMore">): number | undefined {
  return lastPage.hasMore ? lastPage.page + 1 : undefined;
}

/** 合并分页结果并按编号去重（翻页期间数据变动可能让同一条出现在相邻两页，保留先出现的）。 */
export function mergePages<T>(pages: readonly Pick<FaceMatchPage<T>, "items">[], idOf: (item: T) => string): T[] {
  const seen = new Set<string>();
  const merged: T[] = [];
  for (const page of pages) {
    for (const item of page.items) {
      const id = idOf(item);
      if (seen.has(id)) continue;
      seen.add(id);
      merged.push(item);
    }
  }
  return merged;
}
