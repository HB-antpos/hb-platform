// 「抓拍与比对」页面的纯逻辑：筛选 → 查询参数、时间范围换算、判定与通知状态文案映射、黑名单命中流（筛选 / 汇总 / 卡片标签）、
// 列表行模型、缩略图显示判定、详情时间线、去复核判定。
// 不依赖 React Native，可在 Node 下直接测试；文案只返回键（faceMatch 命名空间），由页面翻译。
// 口径：分数是相似度，不等于是同一个人的概率；任何位置都不单独出现一个裸百分比。
import { REVIEW_STATUS_CONFIRMED, REVIEW_STATUS_DISMISSED, REVIEW_STATUS_PENDING } from "./constants";
import { thumbnailKey, type ThumbnailState, type ThumbnailTarget } from "./thumbnail-loader";
import type {
  FaceCaptureHit,
  FaceCaptureResult,
  FaceCaptureResultQuery,
  FaceHitResult,
  FaceHitResultQuery,
  FaceHitStatusKey,
  FaceHitSummary,
  FaceHitSummaryQuery,
  FaceMatchPage,
} from "./types";

/** 与 ui.tsx 的 FaceTone 结构一致（这里不引入 RN 组件文件，保持可在 Node 下测试）。 */
export type CaptureTone = "neutral" | "info" | "success" | "warning" | "danger";

// ───────────── 筛选 chip → 查询参数 ─────────────

/** blacklist = 「黑名单」：数据源换成命中流（listHits），不走抓拍流的任何查询参数。 */
export type CaptureFilterKey = "all" | "blacklist" | "staff" | "newVisitor" | "repeatVisitor";

export const CAPTURE_FILTERS: readonly { key: CaptureFilterKey; labelKey: string }[] = [
  { key: "all", labelKey: "capture.filters.all" },
  { key: "blacklist", labelKey: "capture.filters.blacklist" },
  { key: "staff", labelKey: "capture.filters.staff" },
  { key: "newVisitor", labelKey: "capture.filters.newVisitor" },
  { key: "repeatVisitor", labelKey: "capture.filters.repeatVisitor" },
];

/**
 * 五个 chip 互斥：员工 / 新访客 / 回头客按判定（decision）筛，全部不带任何条件；
 * 「黑名单」走命中流（见 resolveCaptureSource），对抓拍流不产生任何参数。
 * 原来的「仅命中」chip 已由「黑名单」取代（hitOnly 参数仍保留在接口层，页面不再使用）。
 */
export function captureFilterParams(key: CaptureFilterKey): { decision?: string; hitOnly?: boolean } {
  switch (key) {
    case "staff":
    case "newVisitor":
    case "repeatVisitor":
      return { decision: key };
    default:
      return {};
  }
}

export function isBlacklistFilter(key: CaptureFilterKey): boolean {
  return key === "blacklist";
}

// ───────────── 黑名单命中流的状态筛选与汇总 ─────────────

export const HIT_STATUS_KEYS: readonly FaceHitStatusKey[] = ["all", "unread", "pending", "autoConfirmed", "confirmed", "dismissed"];

/** 二级筛选：全部 / 未读 / 待复核 / 自动确认 / 已确认 / 已驳回（顺序即页面顺序）。 */
export const HIT_STATUS_FILTERS: readonly { key: FaceHitStatusKey; labelKey: string }[] = [
  { key: "all", labelKey: "hits.filters.all" },
  { key: "unread", labelKey: "hits.filters.unread" },
  { key: "pending", labelKey: "hits.filters.pending" },
  { key: "autoConfirmed", labelKey: "hits.filters.autoConfirmed" },
  { key: "confirmed", labelKey: "hits.filters.confirmed" },
  { key: "dismissed", labelKey: "hits.filters.dismissed" },
];

/** 入口参数里的初始状态：不认识的值（深链手写、将来新增）一律回落到「全部」。 */
export function normalizeHitStatusKey(value: unknown): FaceHitStatusKey {
  return HIT_STATUS_KEYS.find((key) => key === value) ?? "all";
}

/** 首次进入时选中什么：只有 initialFilter 为 blacklist 时 initialHitStatus 才有意义。 */
export function resolveInitialSelection(initialFilter: "all" | "blacklist" | undefined, initialHitStatus: unknown): { filterKey: CaptureFilterKey; hitStatus: FaceHitStatusKey } {
  if (initialFilter === "blacklist") return { filterKey: "blacklist", hitStatus: normalizeHitStatusKey(initialHitStatus) };
  return { filterKey: "all", hitStatus: "all" };
}

/** 数据源：黑名单 → 命中流（带状态筛选）；其余 → 抓拍流（带判定参数）。 */
export type CaptureSource =
  | { kind: "captures"; params: { decision?: string; hitOnly?: boolean } }
  | { kind: "hits"; hitStatus: FaceHitStatusKey };

export function resolveCaptureSource(filterKey: CaptureFilterKey, hitStatus: FaceHitStatusKey): CaptureSource {
  return isBlacklistFilter(filterKey) ? { kind: "hits", hitStatus } : { kind: "captures", params: captureFilterParams(filterKey) };
}

/** 汇总里某个状态对应的条数；没有汇总返回 null。 */
export function hitStatusCount(summary: FaceHitSummary | null | undefined, key: FaceHitStatusKey): number | null {
  if (!summary) return null;
  switch (key) {
    case "unread":
      return summary.unread;
    case "pending":
      return summary.pending;
    case "autoConfirmed":
      return summary.autoConfirmed;
    case "confirmed":
      return summary.confirmed;
    case "dismissed":
      return summary.dismissed;
    default:
      return summary.total;
  }
}

/** 徽标 / 筛选条上的数字：0、没有、非法值不显示（null）；超过上限写成「99+」。 */
export function formatHitCount(count: number | null | undefined, cap = 99): string | null {
  if (typeof count !== "number" || !Number.isFinite(count) || count <= 0) return null;
  const whole = Math.trunc(count);
  return whole > cap ? `${cap}+` : String(whole);
}

/** 「黑名单」chip 上的红色数字 = 汇总里的未读数（0 / 没有时不显示）。 */
export function blacklistChipBadge(summary: FaceHitSummary | null | undefined): string | null {
  return formatHitCount(summary?.unread, 99);
}

/** 二级筛选 chip：文案键 + 数字（0 或没有时不带数字）。 */
export function describeHitStatusChip(summary: FaceHitSummary | null | undefined, key: FaceHitStatusKey): { labelKey: string; countText: string | null } {
  const labelKey = HIT_STATUS_FILTERS.find((filter) => filter.key === key)?.labelKey ?? "hits.filters.all";
  return { labelKey, countText: formatHitCount(hitStatusCount(summary, key), 999) };
}

export interface HitListBar {
  /** 当前所选状态的总条数；汇总没取到时，已经加载完就用已加载条数，否则 null（不显示）。 */
  total: number | null;
  /** 我的未读数（汇总里的 unread）；没有汇总为 null。 */
  unread: number | null;
  /** 「全部标为已读」是否可点：只有汇总说还有未读才出现（它标的是我的全部命中通知，不受门店 / 时间筛选限制）。 */
  canMarkAll: boolean;
}

/** 列表条「共 N 条 · 未读 M」与「全部标为已读」。 */
export function describeHitListBar(summary: FaceHitSummary | null | undefined, hitStatus: FaceHitStatusKey, loaded: { count: number; hasMore: boolean }): HitListBar {
  const total = summary ? hitStatusCount(summary, hitStatus) : loaded.hasMore ? null : loaded.count;
  const unread = summary ? summary.unread : null;
  return { total, unread, canMarkAll: (unread ?? 0) > 0 };
}

// ───────────── 时间范围 ─────────────

export type CaptureRangeKey = "today" | "yesterday" | "last7Days";

export const CAPTURE_RANGES: readonly { key: CaptureRangeKey; labelKey: string }[] = [
  { key: "today", labelKey: "capture.range.today" },
  { key: "yesterday", labelKey: "capture.range.yesterday" },
  { key: "last7Days", labelKey: "capture.range.last7Days" },
];

function localMidnight(now: Date, dayOffset: number): Date {
  return new Date(now.getFullYear(), now.getMonth(), now.getDate() + dayOffset, 0, 0, 0, 0);
}

/**
 * 范围 → [fromUtc, toUtc)：按手机本地时区的自然日换算成 UTC（门店本地当天的简单近似；
 * 手机与门店不在同一时区时，当天的边界会有偏差，需要精确口径时应由服务端按门店时区出）。
 * 「今天」= 本地今天 0 点到明天 0 点；用 Date 构造器取日界，夏令时切换日也不会错成固定 24 小时偏移。
 */
export function resolveCaptureRange(key: CaptureRangeKey, now: Date): { fromUtc: string; toUtc: string } {
  const startOffset = key === "yesterday" ? -1 : key === "last7Days" ? -6 : 0;
  const endOffset = key === "yesterday" ? 0 : 1;
  return {
    fromUtc: localMidnight(now, startOffset).toISOString(),
    toUtc: localMidnight(now, endOffset).toISOString(),
  };
}

/** 列表请求参数：空值不带（hitOnly=false 也不发，与服务端默认值一致）。 */
export function buildCaptureListParams(query: FaceCaptureResultQuery): {
  storeCode: string | undefined;
  decision: string | undefined;
  hitOnly: boolean | undefined;
  fromUtc: string | undefined;
  toUtc: string | undefined;
  page: number;
  pageSize: number;
} {
  return {
    storeCode: query.storeCode || undefined,
    decision: query.decision || undefined,
    hitOnly: query.hitOnly ? true : undefined,
    fromUtc: query.fromUtc || undefined,
    toUtc: query.toUtc || undefined,
    page: query.page,
    pageSize: query.pageSize,
  };
}

/** 命中流请求参数：全部状态是服务端默认值，不发；空值不带。 */
export function buildHitListParams(query: FaceHitResultQuery): {
  storeCode: string | undefined;
  hitStatus: string | undefined;
  fromUtc: string | undefined;
  toUtc: string | undefined;
  page: number;
  pageSize: number;
} {
  return {
    storeCode: query.storeCode || undefined,
    hitStatus: query.hitStatus && query.hitStatus !== "all" ? query.hitStatus : undefined,
    fromUtc: query.fromUtc || undefined,
    toUtc: query.toUtc || undefined,
    page: query.page,
    pageSize: query.pageSize,
  };
}

/** 命中流汇总的请求参数：只有门店与时间范围（不含状态与分页）。 */
export function buildHitSummaryParams(query: FaceHitSummaryQuery): { storeCode: string | undefined; fromUtc: string | undefined; toUtc: string | undefined } {
  return { storeCode: query.storeCode || undefined, fromUtc: query.fromUtc || undefined, toUtc: query.toUtc || undefined };
}

// ───────────── 判定与分数 ─────────────

const DECISION_PRESENTATION: Record<string, { labelKey: string; tone: CaptureTone }> = {
  staff: { labelKey: "capture.decision.staff", tone: "success" },
  repeatVisitor: { labelKey: "capture.decision.repeatVisitor", tone: "info" },
  newVisitor: { labelKey: "capture.decision.newVisitor", tone: "info" },
  lowQuality: { labelKey: "capture.decision.lowQuality", tone: "warning" },
  noMatch: { labelKey: "capture.decision.noMatch", tone: "neutral" },
};

/** 判定标签：没有评估行显示「未比对」；服务端新增的取值显示「其他判定」，记录不丢。 */
export function describeDecision(result: Pick<FaceCaptureResult, "evaluated" | "decision">): { labelKey: string; tone: CaptureTone } {
  if (!result.evaluated || !result.decision) return { labelKey: "capture.decision.notEvaluated", tone: "neutral" };
  return DECISION_PRESENTATION[result.decision] ?? { labelKey: "capture.decision.other", tone: "neutral" };
}

export function clampPercent(value: number): number {
  return Number.isFinite(value) ? Math.min(100, Math.max(0, Math.round(value))) : 0;
}

export interface CaptureScorePart {
  /** capture.score.staff / capture.score.visit 的文案键。 */
  labelKey: string;
  percent: number;
}

/** 列表行的分数：有哪项显示哪项（员工相似度、访客相似度），都没有返回空数组。 */
export function captureScoreParts(result: Pick<FaceCaptureResult, "staffScorePercent" | "visitScorePercent">): CaptureScorePart[] {
  const parts: CaptureScorePart[] = [];
  if (result.staffScorePercent != null) parts.push({ labelKey: "capture.score.staff", percent: clampPercent(result.staffScorePercent) });
  if (result.visitScorePercent != null) parts.push({ labelKey: "capture.score.visit", percent: clampPercent(result.visitScorePercent) });
  return parts;
}

// ───────────── 命中的通知发送情况 ─────────────

export interface HitNotifyLine {
  labelKey: string;
  params: { count: number; read: number };
  tone: CaptureTone;
  icon: "check" | "clock-outline" | "minus-circle-outline";
}

/**
 * 列表卡片上的通知情况：sent「已通知 N 人 · 已读 M」、pending「排队中」、skipped「已跳过」。
 * 服务端新增的状态取值不认识时返回 null（不展示，不编造含义）。
 */
export function describeHitNotify(hit: Pick<FaceCaptureHit, "notifyState" | "notifyRecipientCount" | "notifyReadCount">): HitNotifyLine | null {
  const params = { count: hit.notifyRecipientCount, read: hit.notifyReadCount };
  switch (hit.notifyState) {
    case "sent":
      return { labelKey: "capture.notify.sent", params, tone: "success", icon: "check" };
    case "pending":
      return { labelKey: "capture.notify.pending", params, tone: "warning", icon: "clock-outline" };
    case "skipped":
      return { labelKey: "capture.notify.skipped", params, tone: "neutral", icon: "minus-circle-outline" };
    default:
      return null;
  }
}

// ───────────── 命中卡片的标签（抓拍流里带 hit 的行与命中流的行共用） ─────────────

export interface HitScoreTag {
  /** 「黑名单命中 N%」里的相似度（夹在 0-100）；不等于是同一个人的概率。 */
  percent: number;
  /** 实心红标签；已驳回（复核判断「不是同一个人」）的命中降成浅色，其余（待复核 / 已确认 / 自动确认）保持实心。 */
  solid: boolean;
}

export function describeHitScoreTag(hit: Pick<FaceCaptureHit, "scorePercent" | "reviewStatus">): HitScoreTag {
  return { percent: clampPercent(hit.scorePercent), solid: hit.reviewStatus !== REVIEW_STATUS_DISMISSED };
}

export interface HitStatusTag {
  labelKey: string;
  tone: CaptureTone;
  /** 自动确认带机器人图标（颜色之外再给一个线索）。 */
  icon: "robot-outline" | null;
}

/**
 * 复核状态标签：待复核（warning）、自动确认（info + 机器人）、已确认（success）、已驳回（neutral）。
 * 文案沿用复核页的 review.status.* / review.autoConfirmedTag；服务端新增的状态取值不认识时返回 null（不编造含义）。
 */
export function describeHitStatusTag(hit: Pick<FaceCaptureHit, "reviewStatus" | "autoConfirmed">): HitStatusTag | null {
  switch (hit.reviewStatus) {
    case REVIEW_STATUS_PENDING:
      return { labelKey: "review.status.pending", tone: "warning", icon: null };
    case REVIEW_STATUS_DISMISSED:
      return { labelKey: "review.status.dismissed", tone: "neutral", icon: null };
    case REVIEW_STATUS_CONFIRMED:
      return hit.autoConfirmed
        ? { labelKey: "review.autoConfirmedTag", tone: "info", icon: "robot-outline" }
        : { labelKey: "review.status.confirmed", tone: "success", icon: null };
    default:
      return null;
  }
}

export interface HitTimelineStep {
  labelKey: string;
  params?: Record<string, number>;
  /** 服务端给出的真实时间（UTC ISO）；没有就是 null，页面不显示时间，绝不编造。 */
  timeUtc: string | null;
  /** done = 已发生（实心点），waiting = 尚未发生或没有发生（空心点）。 */
  state: "done" | "waiting";
}

/**
 * 详情页的通知发送时间线（只有人数，没有姓名）：
 * 命中任务已创建 →（已通知 N 位 / 排队中 / 已跳过）→（已读 M / N）→ 复核状态。
 * 服务端没有「任务创建时间」与「最近已读时间」，对应步骤不带时间；通知状态不认识时省略通知与已读两步。
 */
export function buildHitTimeline(hit: FaceCaptureHit): HitTimelineStep[] {
  const steps: HitTimelineStep[] = [{ labelKey: "capture.timeline.created", timeUtc: null, state: "done" }];
  if (hit.notifyState === "sent") {
    steps.push({ labelKey: "capture.timeline.notified", params: { count: hit.notifyRecipientCount }, timeUtc: hit.notifiedAtUtc, state: "done" });
    steps.push({
      labelKey: "capture.timeline.read",
      params: { read: hit.notifyReadCount, count: hit.notifyRecipientCount },
      timeUtc: null,
      state: hit.notifyReadCount > 0 ? "done" : "waiting",
    });
  } else if (hit.notifyState === "pending") {
    steps.push({ labelKey: "capture.timeline.notifyPending", timeUtc: null, state: "waiting" });
  } else if (hit.notifyState === "skipped") {
    steps.push({ labelKey: "capture.timeline.notifySkipped", timeUtc: null, state: "waiting" });
  }
  if (hit.reviewStatus === REVIEW_STATUS_PENDING) {
    steps.push({ labelKey: "capture.timeline.reviewPending", timeUtc: null, state: "waiting" });
  } else if (hit.reviewStatus === REVIEW_STATUS_DISMISSED) {
    steps.push({ labelKey: "capture.timeline.reviewDismissed", timeUtc: null, state: "done" });
  } else if (hit.reviewStatus === REVIEW_STATUS_CONFIRMED) {
    steps.push({ labelKey: "capture.timeline.reviewConfirmed", timeUtc: null, state: "done" });
  }
  return steps;
}

// ───────────── 列表行模型（抓拍流与命中流共用同一个卡片与详情） ─────────────

/** 行里的命中摘要。canReview：命中流行是服务端算好的；抓拍流行服务端不给，为 null（详情里按权限与图片状态判断）。 */
export type CaptureRowHit = FaceCaptureHit & { canReview: boolean | null };

export interface CaptureRow {
  /** 列表 key：抓拍流 = 抓拍编号；命中流 = 命中任务编号。 */
  key: string;
  source: "capture" | "hit";
  /** 命中流行的留存抓拍可能不存在（Match 模式不留存），所以可能为 null。 */
  captureId: string | null;
  storeCode: string;
  storeName: string | null;
  cameraId: string;
  capturedAtUtc: string;
  isDuplicate: boolean;
  faceCount: number | null;
  /** false = 没有评估行，decision 为 null。 */
  evaluated: boolean;
  decision: string | null;
  staffScorePercent: number | null;
  visitScorePercent: number | null;
  /** 抓拍图编号：只有抓拍流行才有（命中流接口不返回图片编号）。 */
  faceImageId: string | null;
  sceneImageId: string | null;
  hit: CaptureRowHit | null;
}

export function captureResultToRow(result: FaceCaptureResult): CaptureRow {
  return {
    key: result.captureId,
    source: "capture",
    captureId: result.captureId,
    storeCode: result.storeCode,
    storeName: result.storeName,
    cameraId: result.cameraId,
    capturedAtUtc: result.capturedAtUtc,
    isDuplicate: result.isDuplicate,
    faceCount: result.faceCount,
    evaluated: result.evaluated,
    decision: result.decision,
    staffScorePercent: result.staffScorePercent,
    visitScorePercent: result.visitScorePercent,
    faceImageId: result.faceImageId,
    sceneImageId: result.sceneImageId,
    hit: result.hit ? { ...result.hit, canReview: null } : null,
  };
}

export function hitResultToRow(hit: FaceHitResult): CaptureRow {
  return {
    key: hit.taskId,
    source: "hit",
    captureId: hit.captureId,
    storeCode: hit.storeCode,
    storeName: hit.storeName,
    cameraId: hit.cameraId,
    capturedAtUtc: hit.capturedAtUtc,
    isDuplicate: false,
    faceCount: hit.faceCount,
    // 命中流里 decision 有值才说明有对应抓拍与评估。
    evaluated: hit.decision !== null,
    decision: hit.decision,
    staffScorePercent: hit.staffScorePercent,
    visitScorePercent: hit.visitScorePercent,
    faceImageId: null,
    sceneImageId: null,
    hit: {
      taskId: hit.taskId,
      scorePercent: hit.scorePercent,
      band: hit.band,
      reviewStatus: hit.reviewStatus,
      reviewStatusName: hit.reviewStatusName,
      imageAvailable: hit.imageAvailable,
      notifyState: hit.notifyState,
      notifiedAtUtc: hit.notifiedAtUtc,
      notifyRecipientCount: hit.notifyRecipientCount,
      notifyReadCount: hit.notifyReadCount,
      autoConfirmed: hit.autoConfirmed,
      unread: hit.unread,
      notificationId: hit.notificationId,
      canReview: hit.canReview,
    },
  };
}

// ───────────── 缩略图：每行要不要请求、显示什么 ─────────────

export type RowThumbnailPlan =
  /** 需要向服务端取（kind + 编号）。 */
  | { type: "request"; target: ThumbnailTarget }
  /** 没有可取的图：显示占位图标，不请求。 */
  | { type: "placeholder" }
  /** 命中图已过保留期：占位图标 + 「已删除」小条，不请求。 */
  | { type: "gone" };

/**
 * 决定一行的缩略图来源（不发请求）：
 * - 抓拍流行：抓拍有人脸小图编号才取抓拍缩略图（没有就占位，不请求）；
 * - 没有人脸小图但带命中（或命中流行）：取命中图——Match 模式不留存抓拍，命中图仍在；imageAvailable=false 直接显示「已删除」。
 */
export function planRowThumbnail(row: CaptureRow): RowThumbnailPlan {
  if (row.source === "capture" && row.captureId && row.faceImageId) {
    return { type: "request", target: { kind: "capture", id: row.captureId } };
  }
  if (row.hit) {
    return row.hit.imageAvailable && row.hit.taskId
      ? { type: "request", target: { kind: "hit", id: row.hit.taskId } }
      : { type: "gone" };
  }
  return { type: "placeholder" };
}

/** 可见项前后各预取几行：向下滚动更常见，所以后面多取一点。 */
export const THUMBNAIL_PREFETCH = { before: 2, after: 4 } as const;

/**
 * 由「当前可见的行下标」算出要向服务端取的缩略图：可见的在前，其后是向下预取的，最后是向上预取的；
 * 没有可取图的行（占位 / 已删除）与重复项不进入；没有可见行返回空数组。
 */
export function collectThumbnailTargets(
  rows: readonly CaptureRow[],
  visibleIndexes: readonly number[],
  prefetch: { before: number; after: number } = THUMBNAIL_PREFETCH,
): ThumbnailTarget[] {
  const visible = [...new Set(visibleIndexes.filter((index) => Number.isInteger(index) && index >= 0))].sort((a, b) => a - b);
  if (visible.length === 0) return [];
  const first = visible[0];
  const last = visible[visible.length - 1];
  const order = [...visible];
  for (let index = last + 1; index <= last + prefetch.after; index += 1) order.push(index);
  for (let index = first - 1; index >= first - prefetch.before; index -= 1) order.push(index);
  const targets: ThumbnailTarget[] = [];
  const seen = new Set<string>();
  for (const index of order) {
    const row = rows[index];
    if (!row) continue;
    const plan = planRowThumbnail(row);
    if (plan.type !== "request") continue;
    const key = thumbnailKey(plan.target);
    if (seen.has(key)) continue;
    seen.add(key);
    targets.push(plan.target);
  }
  return targets;
}

export type ThumbDisplay =
  | { kind: "image"; uri: string }
  /** 加载中：灰色骨架，不挡住其他信息。 */
  | { kind: "skeleton" }
  | { kind: "placeholder"; deleted: boolean };

/**
 * 头像位显示什么：缩略图被「眼睛」隐藏时一律占位；ok 显示图；加载中显示骨架；
 * none / denied / 加载失败显示占位（不声称已删除）；gone 显示占位 + 「已删除」。
 */
export function describeRowThumbnail(plan: RowThumbnailPlan, state: ThumbnailState | undefined, enabled: boolean): ThumbDisplay {
  if (!enabled || plan.type === "placeholder") return { kind: "placeholder", deleted: false };
  if (plan.type === "gone") return { kind: "placeholder", deleted: true };
  if (!state || state.status === "loading") return { kind: "skeleton" };
  if (state.status === "ok") return { kind: "image", uri: state.uri };
  return { kind: "placeholder", deleted: state.status === "gone" };
}

// ───────────── 标记已读后就地更新缓存里的行 ─────────────

/** 命中流行：未读且编号对得上才改，其余原样返回同一个引用（让 react-query 不无谓地重渲染）。 */
export function markHitResultRead(item: FaceHitResult, taskId: string): FaceHitResult {
  return item.taskId === taskId && item.unread ? { ...item, unread: false } : item;
}

export function markCaptureResultHitRead(item: FaceCaptureResult, taskId: string): FaceCaptureResult {
  return item.hit && item.hit.taskId === taskId && item.hit.unread ? { ...item, hit: { ...item.hit, unread: false } } : item;
}

/** 对分页数据（InfiniteData 形状）逐行映射；没有任何行变化时返回原对象。 */
export function mapPagesItems<T, D extends { pages: readonly FaceMatchPage<T>[] }>(data: D | undefined, map: (item: T) => T): D | undefined {
  if (!data) return data;
  let changed = false;
  const pages = data.pages.map((page) => {
    let pageChanged = false;
    const items = page.items.map((item) => {
      const next = map(item);
      if (next !== item) pageChanged = true;
      return next;
    });
    if (!pageChanged) return page;
    changed = true;
    return { ...page, items };
  });
  return changed ? ({ ...data, pages } as D) : data;
}

// ───────────── 去复核 ─────────────

export interface ReviewJump {
  enabled: boolean;
  /** 不可用时的原因文案键；可用时为 null。 */
  hintKey: string | null;
}

/**
 * 「去复核」是否可用（抓拍流行）：必须有命中、命中抓拍图仍在保留期内，且账号能打开名单页（有复核 / 管理 / 批准权限之一）。
 * 跳转目标见 reviewHref。
 */
export function resolveReviewJump(hit: Pick<FaceCaptureHit, "imageAvailable"> | null, canOpenUnwelcome: boolean): ReviewJump {
  if (!hit) return { enabled: false, hintKey: "capture.detail.reviewHint.noHit" };
  if (!hit.imageAvailable) return { enabled: false, hintKey: "capture.detail.reviewHint.imageGone" };
  if (!canOpenUnwelcome) return { enabled: false, hintKey: "capture.detail.reviewHint.noPermission" };
  return { enabled: true, hintKey: null };
}

/** 合并页「名单 → 命中复核」与「名单」的路径（路由由合并页外壳提供）。 */
export const FACE_HUB_REVIEW_HREF = "/(shell)/face-hub?tab=list&sub=review";
export const FACE_HUB_LIST_HREF = "/(shell)/face-hub?tab=list";

/** 持有复核权限直接进「命中复核」；只有管理 / 批准权限的进名单页。 */
export function reviewHref(hasReviewPermission: boolean): string {
  return hasReviewPermission ? FACE_HUB_REVIEW_HREF : FACE_HUB_LIST_HREF;
}

export interface DetailReviewAction {
  /** 是否显示「去复核」按钮。 */
  visible: boolean;
  enabled: boolean;
  hintKey: string | null;
  href: string;
}

/**
 * 详情里的「去复核」：
 * - 命中流行：只认服务端的 hit.canReview，为 true 才显示（按钮可点），否则不显示；
 * - 抓拍流行：沿用 resolveReviewJump（按钮始终在，不可用时给出原因）。
 */
export function resolveDetailReviewAction(row: Pick<CaptureRow, "source" | "hit">, access: { canOpenUnwelcome: boolean; hasReviewPermission: boolean }): DetailReviewAction {
  const href = reviewHref(access.hasReviewPermission);
  if (row.source === "hit") {
    const allowed = row.hit?.canReview === true;
    return { visible: allowed, enabled: allowed, hintKey: null, href };
  }
  const jump = resolveReviewJump(row.hit, access.canOpenUnwelcome);
  return { visible: true, enabled: jump.enabled, hintKey: jump.hintKey, href };
}

// ───────────── 时间展示（手机本地时区） ─────────────

function pad2(value: number): string {
  return String(value).padStart(2, "0");
}

function clock(date: Date): string {
  return `${pad2(date.getHours())}:${pad2(date.getMinutes())}:${pad2(date.getSeconds())}`;
}

function parse(utcIso: string | null | undefined): Date | null {
  if (!utcIso) return null;
  const date = new Date(utcIso);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** 列表时间：与 now 同一本地日只显示 HH:mm:ss，否则带 MM-dd；无效返回 "-"。 */
export function formatCaptureListTime(utcIso: string | null | undefined, now: Date): string {
  const date = parse(utcIso);
  if (!date) return "-";
  const sameDay = date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate();
  return sameDay ? clock(date) : `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${clock(date)}`;
}

/** 详情时间：yyyy-MM-dd HH:mm:ss；无效返回 "-"。 */
export function formatCaptureFullTime(utcIso: string | null | undefined): string {
  const date = parse(utcIso);
  if (!date) return "-";
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${clock(date)}`;
}
