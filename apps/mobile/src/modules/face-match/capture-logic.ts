// 「抓拍与比对」页面的纯逻辑：筛选 → 查询参数、时间范围换算、判定与通知状态文案映射、详情时间线、去复核判定。
// 不依赖 React Native，可在 Node 下直接测试；文案只返回键（faceMatch 命名空间），由页面翻译。
// 口径：分数是相似度，不等于是同一个人的概率；任何位置都不单独出现一个裸百分比。
import { REVIEW_STATUS_CONFIRMED, REVIEW_STATUS_DISMISSED, REVIEW_STATUS_PENDING } from "./constants";
import type { FaceCaptureHit, FaceCaptureResult, FaceCaptureResultQuery } from "./types";

/** 与 ui.tsx 的 FaceTone 结构一致（这里不引入 RN 组件文件，保持可在 Node 下测试）。 */
export type CaptureTone = "neutral" | "info" | "success" | "warning" | "danger";

// ───────────── 筛选 chip → 查询参数 ─────────────

export type CaptureFilterKey = "all" | "hitOnly" | "staff" | "newVisitor" | "repeatVisitor";

export const CAPTURE_FILTERS: readonly { key: CaptureFilterKey; labelKey: string }[] = [
  { key: "all", labelKey: "capture.filters.all" },
  { key: "hitOnly", labelKey: "capture.filters.hitOnly" },
  { key: "staff", labelKey: "capture.filters.staff" },
  { key: "newVisitor", labelKey: "capture.filters.newVisitor" },
  { key: "repeatVisitor", labelKey: "capture.filters.repeatVisitor" },
];

/** 五个 chip 互斥：仅命中走 hitOnly，其余按判定（decision）筛，全部不带任何条件。 */
export function captureFilterParams(key: CaptureFilterKey): { decision?: string; hitOnly?: boolean } {
  switch (key) {
    case "hitOnly":
      return { hitOnly: true };
    case "staff":
    case "newVisitor":
    case "repeatVisitor":
      return { decision: key };
    default:
      return {};
  }
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

// ───────────── 去复核 ─────────────

export interface ReviewJump {
  enabled: boolean;
  /** 不可用时的原因文案键；可用时为 null。 */
  hintKey: string | null;
}

/**
 * 「去复核」是否可用：必须有命中、命中抓拍图仍在保留期内，且账号能打开名单页（有复核 / 管理 / 批准权限之一）。
 * 目前名单页没有按任务定位的入口，跳转只进列表（默认「待复核」分区）。
 */
export function resolveReviewJump(hit: Pick<FaceCaptureHit, "imageAvailable"> | null, canOpenUnwelcome: boolean): ReviewJump {
  if (!hit) return { enabled: false, hintKey: "capture.detail.reviewHint.noHit" };
  if (!hit.imageAvailable) return { enabled: false, hintKey: "capture.detail.reviewHint.imageGone" };
  if (!canOpenUnwelcome) return { enabled: false, hintKey: "capture.detail.reviewHint.noPermission" };
  return { enabled: true, hintKey: null };
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
